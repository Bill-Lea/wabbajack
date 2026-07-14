using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ReactiveUI;
using Wabbajack.Common;
using Wabbajack.Configuration;
using Wabbajack.Downloaders;
using Wabbajack.DTOs;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Networking.Http;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.Services.OSIntegrated.Services;

namespace Wabbajack;

public class LauncherUpdater
{
    private readonly ILogger<LauncherUpdater> _logger;
    private readonly HttpClient _client;
    private readonly Client _wjclient;
    private readonly DTOSerializer _dtos;
    private readonly MainSettings _settings;

    private readonly DownloadDispatcher _downloader;

    /// <summary>Command-line flag that bypasses the update confirmation for automated runs.</summary>
    public const string UnattendedUpdateArg = "--unattended-update";

    private readonly bool _unattended = Environment.GetCommandLineArgs()
        .Any(a => string.Equals(a, UnattendedUpdateArg, StringComparison.OrdinalIgnoreCase));

    private static Uri GITHUB_REPO_RELEASES = new("https://api.github.com/repos/wabbajack-tools/wabbajack/releases");

    public LauncherUpdater(ILogger<LauncherUpdater> logger, HttpClient client, Client wjclient, DTOSerializer dtos,
        DownloadDispatcher downloader, MainSettings settings)
    {
        _logger = logger;
        _client = client;
        _wjclient = wjclient;
        _dtos = dtos;
        _downloader = downloader;
        _settings = settings;
    }


    public static Lazy<AbsolutePath> CommonFolder = new (() =>
    {
        var entryPoint = KnownFolders.EntryPoint;

        // If we're not in a folder that looks like a version, abort
        if (!Version.TryParse(entryPoint.FileName.ToString(), out var version))
        {
            return entryPoint;
        }

        // If we're not in a folder that has Wabbajack.exe in the parent folder, abort
        if (!entryPoint.Parent.Combine(Consts.AppName).WithExtension(new Extension(".exe")).FileExists())
        {
            return entryPoint;
        }

        return entryPoint.Parent;
    });



    public async Task Run()
    {

        if (CommonFolder.Value == KnownFolders.EntryPoint)
        {
            _logger.LogInformation("Outside of standard install folder, not updating");
            return;
        }

        var version = Version.Parse(KnownFolders.EntryPoint.FileName.ToString());

        // Pull downloaded modlist installers out of the per-version folders and into the shared folder
        // BEFORE any old version folders are deleted below, so nothing is lost on update.
        await ConsolidateDownloadedModLists();

        var oldVersions = CommonFolder.Value
            .EnumerateDirectories()
            .Select(f => Version.TryParse(f.FileName.ToString(), out var ver) ? (ver, f) : default)
            .Where(f => f != default)
            .Where(f => f.ver < version)
            .Select(f => f!)
            .OrderByDescending(f => f)
            .Skip(2)
            .ToArray();

        foreach (var (_, path) in oldVersions)
        {
            _logger.LogInformation("Deleting old Wabbajack version at: {Path}", path);
            path.DeleteDirectory();
        }

        var release = (await GetReleases())
            .Select(release => Version.TryParse(release.Tag, out version) ? (version, release) : default)
            .Where(r => r != default)
            .OrderByDescending(r => r.version)
            .Select(r =>
            {
                var (version, release) = r;
                var asset = release.Assets.FirstOrDefault(a => a.Name == "Wabbajack.exe");
                return asset != default ? (version, release, asset) : default;
            })
            .FirstOrDefault();

        var launcherFolder = KnownFolders.EntryPoint.Parent;
        var exePath = launcherFolder.Combine("Wabbajack.exe");

        var launcherVersion = FileVersionInfo.GetVersionInfo(exePath.ToString());

        if (release != default && release.version > Version.Parse(launcherVersion.FileVersion!))
        {
            _logger.LogInformation("Launcher update detected: {OldVersion} -> {NewVersion}", launcherVersion.FileVersion, release.version);

            // Gate the launcher-stub replacement behind the same AskBeforeApplicationUpdate setting.
            // A newer launcher stub always has a local copy present (the running Wabbajack.exe), so the
            // fresh-install case never applies here.
            var decision = ApplicationUpdateDecider.Decide(Version.Parse(launcherVersion.FileVersion!),
                release.version, _settings.AskBeforeApplicationUpdate, _unattended);

            switch (decision.Reason)
            {
                case UpdateBypassReason.UnattendedFlag:
                    _logger.LogInformation("Unattended update requested ({Arg}); replacing launcher without prompting", UnattendedUpdateArg);
                    break;
                case UpdateBypassReason.SettingDisabled:
                    _logger.LogInformation("AskBeforeApplicationUpdate disabled; replacing launcher without prompting");
                    break;
            }

            if (decision.Action == UpdateAction.AskUser)
            {
                var proceed = await ConfirmUpdate(launcherVersion.FileVersion!, release.version);
                if (!proceed)
                {
                    _logger.LogInformation("Launcher update to {NewVersion} declined by user; skipping replacement for this session", release.version);
                    return;
                }

                _logger.LogInformation("Launcher update to {NewVersion} accepted by user", release.version);
            }

            _logger.LogInformation("Updating Launcher from {OldVersion} to {NewVersion}", launcherVersion.FileVersion, release.version);
            var tempPath = launcherFolder.Combine("Wabbajack.exe.temp");

            await _downloader.Download(new Archive
            {
                State = new Http {Url = release.asset.BrowserDownloadUrl!},
                Name = release.asset.Name,
                Size = release.asset.Size
            }, tempPath, CancellationToken.None);

            if (tempPath.Size() != release.asset.Size)
            {
                _logger.LogInformation(
                    "Downloaded launcher did not match expected size: {DownloadedSize} expected {ExpectedSize}", tempPath.Size(), release.asset.Size);
                return;
            }

            if (exePath.FileExists())
                exePath.Delete();
            await tempPath.MoveToAsync(exePath, true, CancellationToken.None);

            _logger.LogInformation("Finished updating wabbajack");
            await _wjclient.SendMetric("updated_launcher", $"{launcherVersion.FileVersion} -> {release.version}");
        }
    }

    /// <summary>
    /// Historically each version folder kept its own <c>downloaded_mod_lists</c>, so downloaded modlist
    /// installers were invisible to newer versions and eventually deleted with old version folders. This
    /// migrates any such files up into the version-independent shared folder (the launcher common folder),
    /// where the app now stores and looks for them. Newest-version copies win on conflict; runs are
    /// idempotent and best-effort (failures are logged, never fatal).
    /// </summary>
    private async Task ConsolidateDownloadedModLists()
    {
        await DownloadedModListConsolidator.Consolidate(CommonFolder.Value, _logger);
    }

    /// <summary>
    /// Shows the shared WPF confirmation dialog on the UI thread and returns the user's choice.
    /// Mirrors the existing pattern used by InstallationVM for reaching the active MainWindowVM.
    /// </summary>
    private async Task<bool> ConfirmUpdate(string currentVersion, Version availableVersion)
    {
        var tcs = new TaskCompletionSource<bool>();
        RxApp.MainThreadScheduler.Schedule(async () =>
        {
            try
            {
                var mainWindowVM = (MainWindowVM)System.Windows.Application.Current.MainWindow!.DataContext;
                var result = await mainWindowVM.ShowConfirmationDialog(
                    "Wabbajack Update Available",
                    "A new version of Wabbajack is available.\n\n" +
                    $"Current version: {currentVersion}\n" +
                    $"Available version: {availableVersion}\n\n" +
                    "Would you like to update now?");
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                // If the dialog cannot be shown, err on the side of not silently updating.
                _logger.LogError(ex, "Failed to show launcher update confirmation dialog; skipping update");
                tcs.TrySetResult(false);
            }
        });
        return await tcs.Task;
    }

    private async Task<Release[]> GetReleases()
    {
        _logger.LogInformation("Getting new Wabbajack version list");
        var msg = MakeMessage(GITHUB_REPO_RELEASES);
        return await _client.GetJsonFromSendAsync<Release[]>(msg, _dtos.Options);
    }

    private HttpRequestMessage MakeMessage(Uri uri)
    {
        var msg =  new HttpRequestMessage(HttpMethod.Get, uri);
        msg.AddChromeAgent();
        return msg;
    }


    class Release
    {
        [JsonProperty("tag_name")] public string Tag { get; set; } = "";

        [JsonProperty("assets")] public Asset[] Assets { get; set; } = Array.Empty<Asset>();

    }

    class Asset
    {
        [JsonProperty("browser_download_url")]
        public Uri? BrowserDownloadUrl { get; set; }

        [JsonProperty("name")] public string Name { get; set; } = "";

        [JsonProperty("size")] public long Size { get; set; } = 0;
    }
}
