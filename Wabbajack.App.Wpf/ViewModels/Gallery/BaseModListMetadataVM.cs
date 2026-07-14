using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Wabbajack.Common;
using Wabbajack.DTOs;
using Wabbajack.DTOs.ModListValidation;
using Wabbajack.Messages;
using Wabbajack.Models;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.Services.OSIntegrated.Services;

namespace Wabbajack;


public readonly record struct ModListTag(string name)
{
    public string Name { get; } = name;
    public override string ToString() => Name;
}

public readonly record struct ModListMod(string name)
{
    public string Name { get; } = name;
    public override string ToString() => Name;
}

public class BaseModListMetadataVM : ViewModel
{
    public ModlistMetadata Metadata { get; }
    public AbsolutePath Location { get; }
    public LoadingLock LoadingImageLock { get; } = new();
    [Reactive] public HashSet<ModListTag> ModListTagList { get; protected set; }
    [Reactive] public Percent ProgressPercent { get; set; }
    [Reactive] public bool IsBroken { get; protected set; }
    [Reactive] public ModListStatus Status { get; set; }

    // Update-availability of the locally downloaded installer relative to the gallery.
    [Reactive] public ModListUpdateState UpdateState { get; set; } = ModListUpdateState.Unknown;
    [Reactive] public bool UpdateAvailable { get; set; }
    // Whether the update badge/indicator should be shown at all (only when a local file exists).
    [Reactive] public bool ShowUpdateBadge { get; set; }
    [Reactive] public string UpdateStatusText { get; set; } = string.Empty;
    [Reactive] public string UpdateStatusTooltip { get; set; } = string.Empty;
    [Reactive] public string DownloadedVersionText { get; set; } = string.Empty;
    [Reactive] public string AvailableVersionText { get; set; } = string.Empty;
    [Reactive] public bool IsDownloading { get; protected set; }
    [Reactive] public string DownloadSizeText { get; protected set; }
    [Reactive] public string InstallSizeText { get; protected set; }
    [Reactive] public string TotalSizeRequirementText { get; protected set; }
    [Reactive] public string VersionText { get; set; }
    [Reactive] public bool ImageContainsTitle { get; protected set; }
    [Reactive] public GameMetaData GameMetaData { get; protected set; }
    [Reactive] public bool DisplayVersionOnlyInInstallerView { get; protected set; }

    [Reactive] public ICommand DetailsCommand { get; set; }
    [Reactive] public ICommand InstallCommand { get; protected set; }

    [Reactive] public IValidationResult Error { get; protected set; }

    protected ObservableAsPropertyHelper<BitmapImage> _Image { get; set; }
    public BitmapImage Image => _Image.Value;

    protected ObservableAsPropertyHelper<bool> _LoadingImage { get; set; }
    public bool LoadingImage => _LoadingImage.Value;

    public ModListSummary? Summary { get; set; }

    protected Subject<bool> IsLoadingIdle;
    protected readonly ILogger _logger;
    protected readonly ModListDownloadMaintainer _maintainer;
    protected readonly ModListUpdateService _updateService;
    protected readonly Client _wjClient;
    protected readonly CancellationToken _cancellationToken;
    protected readonly ServiceProvider _serviceProvider;
    protected readonly ImageCacheManager _icm;

    public BaseModListMetadataVM(ILogger logger, ModlistMetadata metadata,
        ModListDownloadMaintainer maintainer, ModListUpdateService updateService, ModListSummary? summary, Client wjClient, CancellationToken cancellationToken, HttpClient client, ImageCacheManager icm)
    {
        _logger = logger;
        _maintainer = maintainer;
        _updateService = updateService;
        Metadata = metadata;
        Summary = summary;
        _wjClient = wjClient;
        _cancellationToken = cancellationToken;

        GameMetaData = Metadata.Game.MetaData();
        Location = LauncherUpdater.CommonFolder.Value.Combine("downloaded_mod_lists", Metadata.NamespacedName).WithExtension(Ext.Wabbajack);
        
        UpdateStatus().FireAndForget();

        ModListTagList = Metadata.Tags?.Select(tag => new ModListTag(tag)).ToHashSet();
        ModListTagList.Add(new ModListTag(GameMetaData.HumanFriendlyGameName));

        DownloadSizeText = "Download size: " + UIUtils.FormatBytes(Metadata.DownloadMetadata.SizeOfArchives);
        InstallSizeText = "Installation size: " + UIUtils.FormatBytes(Metadata.DownloadMetadata.SizeOfInstalledFiles);
        TotalSizeRequirementText =  "Total size requirement: " + UIUtils.FormatBytes( Metadata.DownloadMetadata.TotalSize );
        VersionText = "v" + Metadata.Version;
        ImageContainsTitle = Metadata.ImageContainsTitle;
        DisplayVersionOnlyInInstallerView = Metadata.DisplayVersionOnlyInInstallerView;
        IsBroken = (Summary?.HasFailures ?? false) || metadata.ForceDown;

        IsLoadingIdle = new Subject<bool>();

        var smallImageUri = UIUtils.GetLargeImageUri(metadata);
        var imageObs = Observable.Return(smallImageUri)
            .DownloadBitmapImage(
                (ex) => _logger.LogError("Error downloading modlist image {Title} from {ImageUri}: {Exception}",
                    Metadata.Title, smallImageUri, ex.ToString()), LoadingImageLock, client, icm);

            _Image = imageObs
                .ToGuiProperty(this, nameof(Image))
                .DisposeWith(CompositeDisposable);

            _LoadingImage = imageObs
                .Select(x => false)
                .StartWith(true)
                .ToGuiProperty(this, nameof(LoadingImage))
                .DisposeWith(CompositeDisposable);

        InstallCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (await _maintainer.HaveModList(Metadata))
            {
                Install();
            }
            else
            {
                await Download();
                Install();
            }
        }, LoadingLock.WhenAnyValue(ll => ll.IsLoading)
            .CombineLatest(this.WhenAnyValue(vm => vm.IsBroken))
            .Select(v => !v.First && !v.Second));

        DetailsCommand = ReactiveCommand.Create(() => {
            LoadModlistForDetails.Send(this);
            ShowFloatingWindow.Send(FloatingScreenType.ModListDetails);
        });
    }

    private void Install()
    {
        LoadModlistForInstalling.Send(_maintainer.ModListPath(Metadata), Metadata);
        NavigateToGlobal.Send(ScreenType.Installer);
        ShowFloatingWindow.Send(FloatingScreenType.None);
    }

    protected async Task Download()
    {
        try
        {
            Status = ModListStatus.Downloading;

            using var ll = LoadingLock.WithLoading();
            var (progress, task) = _maintainer.DownloadModlist(Metadata, _cancellationToken);
            var dispose = progress
                .BindToStrict(this, vm => vm.ProgressPercent);
            try
            {
                await _wjClient.SendMetric("downloading", Metadata.Title);
                await task;
                await UpdateStatus();
            }
            finally
            {
                dispose.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "While downloading {Modlist}", Metadata.RepositoryName);
            await UpdateStatus();
        }
    }

    protected async Task UpdateStatus()
    {
        if (await _maintainer.HaveModList(Metadata))
            Status = ModListStatus.Downloaded;
        else if (LoadingLock.IsLoading)
            Status = ModListStatus.Downloading;
        else
            Status = ModListStatus.NotDownloaded;

        await RefreshUpdateStatus();
    }

    /// <summary>
    ///     Compares the locally downloaded installer (if any) against the current gallery version and
    ///     surfaces an "update available" badge. Only the small <c>.metadata</c> sidecar is read (cached);
    ///     the large <c>.wabbajack</c> archive is never opened here. Safe to call on any refresh — it never
    ///     blocks the UI thread and swallows failures into a <see cref="ModListUpdateState.CheckFailed"/> badge.
    /// </summary>
    protected async Task RefreshUpdateStatus()
    {
        // A local file must exist before an update badge makes sense.
        var localExists = await _maintainer.HaveModList(Metadata)
                          || _maintainer.ModListPath(Metadata).FileExists();

        if (!localExists)
        {
            ApplyUpdateResult(new ModListUpdateResult(ModListUpdateState.Unknown, null, null), show: false);
            return;
        }

        var result = await _updateService.CheckForUpdateAsync(_maintainer.ModListPath(Metadata), Metadata, _cancellationToken);
        RxApp.MainThreadScheduler.Schedule(() => ApplyUpdateResult(result, show: true));
    }

    private void ApplyUpdateResult(ModListUpdateResult result, bool show)
    {
        UpdateState = result.State;
        UpdateAvailable = result.IsUpdateAvailable;
        ShowUpdateBadge = show;

        DownloadedVersionText = result.LocalVersion is not null ? $"Downloaded: v{result.LocalVersion}" : string.Empty;
        AvailableVersionText = result.OnlineVersion is not null ? $"Available: v{result.OnlineVersion}" : string.Empty;

        (UpdateStatusText, UpdateStatusTooltip) = result.State switch
        {
            ModListUpdateState.UpdateAvailable => ("UPDATE AVAILABLE",
                "A newer modlist installer is available. Review the modlist changelog before reinstalling."),
            ModListUpdateState.Current => ("CURRENT",
                "You have the latest published version of this modlist installer."),
            ModListUpdateState.Unlisted => ("NOT IN GALLERY",
                "This downloaded modlist is no longer listed in the gallery. It is kept and can still be installed."),
            ModListUpdateState.CheckFailed => ("STATUS UNKNOWN",
                "Could not check for a newer version. Verify your connection and refresh the gallery."),
            _ => ("STATUS UNKNOWN",
                "The installed version could not be determined, so update availability is unknown."),
        };
    }

    public enum ModListStatus
    {
        NotDownloaded,
        Downloading,
        Downloaded
    }
}
