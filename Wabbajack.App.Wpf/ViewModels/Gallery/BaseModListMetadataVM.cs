using System;
using System.Collections.Generic;
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
using ReactiveUI.SourceGenerators;

using Wabbajack.Common;
using Wabbajack.DTOs;
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

public partial class BaseModListMetadataVM : ViewModel
{
    public ModlistMetadata Metadata { get; }
    public AbsolutePath Location { get; }
    public LoadingLock LoadingImageLock { get; } = new();
    [Reactive] public partial HashSet<ModListTag> ModListTagList { get; protected set; }
    [Reactive] public partial Percent ProgressPercent { get; set; }
    [Reactive] public partial bool IsBroken { get; protected set; }
    [Reactive] public partial ModListStatus Status { get; set; }

    // Update-availability of the locally downloaded installer relative to the gallery.
    [Reactive] public partial ModListUpdateState UpdateState { get; set; } = ModListUpdateState.Unknown;
    [Reactive] public partial bool UpdateAvailable { get; set; }
    // Whether the update badge/indicator should be shown at all (only when a local file exists).
    [Reactive] public partial bool ShowUpdateBadge { get; set; }
    [Reactive] public partial string UpdateStatusText { get; set; } = string.Empty;
    [Reactive] public partial string UpdateStatusTooltip { get; set; } = string.Empty;
    [Reactive] public partial string DownloadedVersionText { get; set; } = string.Empty;
    [Reactive] public partial string AvailableVersionText { get; set; } = string.Empty;
    [Reactive] public partial bool IsDownloading { get; protected set; }
    [Reactive] public partial string DownloadSizeText { get; protected set; }
    [Reactive] public partial string InstallSizeText { get; protected set; }
    [Reactive] public partial string TotalSizeRequirementText { get; protected set; }
    [Reactive] public partial string VersionText { get; set; }
    [Reactive] public partial bool ImageContainsTitle { get; protected set; }
    [Reactive] public partial GameMetaData GameMetaData { get; protected set; }
    [Reactive] public partial bool DisplayVersionOnlyInInstallerView { get; protected set; }

    [Reactive] public partial ICommand DetailsCommand { get; set; }
    [Reactive] public partial ICommand InstallCommand { get; protected set; }

    [Reactive] public partial IValidationResult Error { get; protected set; }

    protected ObservableAsPropertyHelper<BitmapImage> _Image { get; set; }
    public BitmapImage Image => _Image.Value;

    protected ObservableAsPropertyHelper<bool> _LoadingImage { get; set; }
    public bool LoadingImage => _LoadingImage.Value;

    protected ObservableAsPropertyHelper<BitmapImage> _GameIcon { get; set; }
    public BitmapImage GameIcon => _GameIcon.Value;

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
        ModListDownloadMaintainer maintainer, ModListUpdateService updateService, ModListSummary? summary, Client wjClient, CancellationToken cancellationToken, HttpClient client, ImageCacheManager icm, GameIconCache gameIcons)
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
        TotalSizeRequirementText = "Total size requirement: " + UIUtils.FormatBytes(Metadata.DownloadMetadata.TotalSize);
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
            .ToGuiProperty(this, nameof(Image), deferSubscription: true)
            .DisposeWith(CompositeDisposable);

        _LoadingImage = LoadingImageLock.WhenAnyValue(x => x.IsLoading)
            .ToGuiProperty(this, nameof(LoadingImage))
            .DisposeWith(CompositeDisposable);

        _GameIcon = gameIcons.Get(GameMetaData.IconSource)
            .ToGuiProperty(this, nameof(GameIcon))
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

        DetailsCommand = ReactiveCommand.Create(() =>
        {
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
