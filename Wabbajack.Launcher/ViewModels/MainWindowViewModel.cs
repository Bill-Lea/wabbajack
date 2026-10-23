using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using JetBrains.Annotations;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Models;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Wabbajack.Common;
using Wabbajack.Configuration;
using Wabbajack.Downloaders.Http;
using Wabbajack.DTOs.Logins;
using Wabbajack.Networking.Http.Interfaces;
using Wabbajack.Networking.NexusApi;
using Wabbajack.Paths;
using Wabbajack.Launcher.Platforms;
using Wabbajack.Paths.IO;
#pragma warning disable SYSLIB0014

namespace Wabbajack.Launcher.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly WebClient _client = new();
    private readonly List<string> _errors = new();

    private (Version Version, long Size, Func<Task<Uri>> Uri) _version;
    // WABBAJACK_LAUNCHER_RELEASES points the launcher at another release list, to test a launcher against
    // releases that are not published; unset, it reads Wabbajack's own.
    public Uri GITHUB_REPO = new(Environment.GetEnvironmentVariable("WABBAJACK_LAUNCHER_RELEASES") is {Length: > 0} url
        ? url
        : "https://api.github.com/repos/wabbajack-tools/wabbajack/releases");
    private readonly NexusApi _nexusApi;
    private readonly HttpDownloader _downloader;
    private readonly ITokenProvider<NexusOAuthState> _tokenProvider;
    private readonly MainSettings _settings;

    /// <summary>Command-line flag that bypasses the update confirmation for automated runs.</summary>
    public const string UnattendedUpdateArg = "--unattended-update";

    private readonly bool _unattended = Environment.GetCommandLineArgs()
        .Any(a => string.Equals(a, UnattendedUpdateArg, StringComparison.OrdinalIgnoreCase));

    // The launcher clears all logging providers (see Program.cs), so it writes its own diagnostic
    // log directly to disk. This records the update-decision path so a run can be diagnosed after
    // the fact (which version was found, the decision + reason, and the user's prompt choice).
    private readonly AbsolutePath _logFile =
        KnownFolders.LauncherAwarePath.Combine("logs").Combine("wabbajack-launcher.current.log");
    private readonly object _logLock = new();

    public MainWindowViewModel(NexusApi nexusApi, HttpDownloader downloader, ITokenProvider<NexusOAuthState> tokenProvider, MainSettings settings)
    {
        AppDomain.CurrentDomain.UnhandledException += async (sender, e) =>
        {
            var msg = MsBox.Avalonia.MessageBoxManager
                .GetMessageBoxStandard(new MessageBoxStandardParams()
                {
                    Topmost = true,
                    ShowInCenter = true,
                    ContentTitle = "Wabbajack Launcher: Uncaught error",
                    ContentMessage =
                        $"Uncaught exception! For help, please share this in the Wabbajack Discord. Exception: {((Exception)e.ExceptionObject).ToString()}"
                });
            var result = await msg.ShowAsync();
        };

        _nexusApi = nexusApi;
        Status = "Checking for new versions";
        _downloader = downloader;
        _tokenProvider = tokenProvider;
        _settings = settings;

        this.WhenAnyValue(vm => vm.Status)
            .Buffer(2, 1)
            .Select(b => b[0])
            .Subscribe(s => {
                if (s.StartsWith("Extracting") || s.StartsWith("Downloading")) return;

                if (StatusHistory.Count >= 3)
                    StatusHistory.RemoveAt(0);

                StatusHistory.Add(s);
            });
        var tsk = CheckForUpdates();
    }

    [Reactive] public partial ObservableCollection<string> StatusHistory { get; set; } = new ObservableCollection<string>();
    [Reactive] public partial string Status { get; set; }

    /// <summary>
    /// Starts a fresh log for this run, rolling the previous run's log aside. Logging must never
    /// break startup, so every failure here is swallowed.
    /// </summary>
    private void InitLog()
    {
        try
        {
            var dir = _logFile.Parent.ToString();
            Directory.CreateDirectory(dir);

            // Keep exactly one prior run's log alongside the current one.
            if (File.Exists(_logFile.ToString()))
            {
                var prev = _logFile.Parent.Combine("wabbajack-launcher.previous.log").ToString();
                try { if (File.Exists(prev)) File.Delete(prev); } catch { /* ignored */ }
                try { File.Move(_logFile.ToString(), prev); } catch { /* ignored */ }
            }

            File.WriteAllText(_logFile.ToString(),
                $"############ Wabbajack Launcher log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ############{Environment.NewLine}");
        }
        catch { /* ignored - logging is best-effort */ }
    }

    /// <summary>Appends a single timestamped line to the current run's log. Never throws.</summary>
    private void LogLine(string message)
    {
        try
        {
            lock (_logLock)
                File.AppendAllText(_logFile.ToString(),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* ignored - logging is best-effort */ }
    }

    private async Task CheckForUpdates()
    {
        InitLog();
        LogLine($"Launcher started. entryPoint={KnownFolders.EntryPoint}, cwd={KnownFolders.CurrentDirectory}, " +
                $"unattended={_unattended}, AskBeforeApplicationUpdate={_settings.AskBeforeApplicationUpdate}");

        await VerifyCurrentLocation();

        _client.Headers.Add("user-agent", "Wabbajack Launcher");
        Status = "Selecting Release";

        try
        {

            if (_tokenProvider.HaveToken())
            {
                try
                {
                    _version = await GetNexusReleases(CancellationToken.None);
                }
                catch (Exception)
                {
                    _errors.Add("Nexus error");
                }
            }

            if (_version == default)
            {
                _version = await GetGithubRelease(CancellationToken.None);
            }


        }
        catch (Exception ex)
        {
            LogLine($"ERROR selecting release: {ex.Message}");
            _errors.Add(ex.Message);
            await FinishAndExit();
        }

        if (_version == default)
        {
            LogLine("Unable to find releases from any source.");
            _errors.Add("Unable to find releases");
            await FinishAndExit();
        }

        LogLine($"Latest available version: {_version.Version}");
        Status = "Looking for Updates";

        var baseFolder = Platform.InstallRoot.Combine(_version.Version.ToString());

        if (baseFolder.Combine(Platform.AppExecutable).FileExists())
        {
            LogLine($"Latest version {_version.Version} already present at {baseFolder}; launching without update.");
            await FinishAndExit();
        }



        // A newer version has been identified and no local copy of it exists yet. Decide whether to
        // ask the user before downloading/installing it. FinishAndExit() launches the highest local
        // version, so declining naturally continues into the currently installed version.
        var installedVersion = GetHighestInstalledVersion();
        var decision = ApplicationUpdateDecider.Decide(installedVersion, _version.Version,
            _settings.AskBeforeApplicationUpdate, _unattended);

        LogLine($"Highest installed version: {installedVersion?.ToString() ?? "none"}");
        LogLine($"Update decision: action={decision.Action}, reason={decision.Reason}");

        if (decision.Action == UpdateAction.NoUpdate)
        {
            Status = $"No newer version than {installedVersion} available; launching current version.";
            LogLine(Status);
            await FinishAndExit();
            return;
        }

        switch (decision.Reason)
        {
            case UpdateBypassReason.FreshInstall:
                Status = $"No local version found; installing {_version.Version} without prompting.";
                break;
            case UpdateBypassReason.UnattendedFlag:
                Status = $"Unattended update requested ({UnattendedUpdateArg}); updating to {_version.Version} without prompting.";
                break;
            case UpdateBypassReason.SettingDisabled:
                Status = $"Update prompt disabled in settings; updating to {_version.Version} without prompting.";
                break;
        }
        if (decision.Reason != UpdateBypassReason.None)
            LogLine(Status);

        if (decision.Action == UpdateAction.AskUser)
        {
            LogLine($"Prompting user to update from {installedVersion?.ToString() ?? "none"} to {_version.Version}.");
            var proceed = await PromptForUpdate(installedVersion, _version.Version);
            LogLine($"User chose: {(proceed ? "Update now" : "Not now")}.");
            if (!proceed)
            {
                Status = $"Update to {_version.Version} declined; launching current version.";
                LogLine(Status);
                await FinishAndExit();
                return;
            }

            Status = $"Update to {_version.Version} accepted.";
        }

        LogLine($"Proceeding to download version {_version.Version}.");
        Status = $"Getting download Uri for {_version.Version}";
        var uri = await _version.Uri();



        var wc = new WebClient();
        wc.DownloadProgressChanged += (sender, args) => UpdateProgress($"{_version.Version}", sender, args);
        Status = $"Downloading {_version.Version} ...";
        byte[] data;
        LogLine($"Downloading {_version.Version} from {uri}");
        try
        {
            data = await wc.DownloadDataTaskAsync(uri);
        }
        catch (Exception ex)
        {
            LogLine($"Download failed ({ex.Message}); retrying once.");
            _errors.Add(ex.Message);
            // Something went wrong so fallback to original URL
            try
            {
                data = await wc.DownloadDataTaskAsync(uri);
            }
            catch (Exception ex2)
            {
                LogLine($"Download retry failed: {ex2.Message}");
                _errors.Add(ex2.Message);
                await FinishAndExit();
                throw; // avoid unsigned variable 'data'
            }
        }

        LogLine($"Downloaded {data.Length} bytes; extracting to {baseFolder}");

        try
        {
            Status = $"Extracting {_version.Version}";
            Platform.Unpack(data, baseFolder);
        }
        catch (Exception ex)
        {
            _errors.Add(ex.Message);
        }

        await FinishAndExit();
    }

    /// <summary>
    /// The highest version-named folder present in the current directory (the same set of folders
    /// <see cref="FinishAndExit"/> launches from), or null when nothing is installed yet.
    /// </summary>
    private static Version? GetHighestInstalledVersion()
    {
        return KnownFolders.CurrentDirectory.EnumerateDirectories(recursive: false)
            .Select(v => Version.TryParse(v.FileName.ToString(), out var ver) ? ver : null)
            .Where(v => v != null)
            .OrderByDescending(v => v)
            .FirstOrDefault();
    }

    /// <summary>
    /// Shows the update confirmation dialog on the Avalonia UI thread. Returns true when the user
    /// chooses to update now, false otherwise.
    /// </summary>
    private static async Task<bool> PromptForUpdate(Version? installedVersion, Version availableVersion)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var box = MsBox.Avalonia.MessageBoxManager.GetMessageBoxCustom(new MessageBoxCustomParams
            {
                Topmost = true,
                ShowInCenter = true,
                ContentTitle = "Wabbajack Update Available",
                ContentMessage =
                    "A new version of Wabbajack is available.\n\n" +
                    $"Currently installed version: {installedVersion?.ToString() ?? "none"}\n" +
                    $"Available version: {availableVersion}\n\n" +
                    "Would you like to update now?",
                ButtonDefinitions = new[]
                {
                    new ButtonDefinition { Name = "Update now", IsDefault = true },
                    new ButtonDefinition { Name = "Not now", IsCancel = true }
                }
            });

            var result = await box.ShowAsync();
            return result == "Update now";
        });
    }

    private async Task VerifyCurrentLocation()
    {
        try
        {
            var entryPoint = KnownFolders.EntryPoint;
            if (KnownFolders.IsInSpecialFolder(entryPoint, out var _) || entryPoint.Depth <= 1)
            {
                var msg = MsBox.Avalonia.MessageBoxManager
                    .GetMessageBoxStandard(new MessageBoxStandardParams()
                    {
                        Topmost = true,
                        ShowInCenter = true,
                        ContentTitle = "Wabbajack Launcher: Bad startup path",
                        ContentMessage =
                            "Cannot start in the root of a drive, or protected folder locations such as Downloads, Desktop etc.\nPlease move Wabbajack to another folder, creating a new folder if necessary ( example : C:\\Wabbajack\\), outside of these locations."
                    });
                var result = await msg.ShowAsync();
                Environment.Exit(1);
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            var msg = MsBox.Avalonia.MessageBoxManager
                .GetMessageBoxStandard(new MessageBoxStandardParams()
                {
                    Topmost = true,
                    ShowInCenter = true,
                    ContentTitle = "Wabbajack Launcher: Error",
                    ContentMessage = ex.ToString()
                });
            var result = await msg.ShowAsync();
            Environment.Exit(1);
        }
    }

    [ContractAnnotation("=> halt")]
    private async Task FinishAndExit()
    {
        try
        {
            Status = "Launching...";
            var wjFolder = Platform.InstallRoot.EnumerateDirectories(recursive:false)
                .OrderByDescending(v =>
                    Version.TryParse(v.FileName.ToString(), out var ver) ? ver : new Version(0, 0, 0, 0))
                .FirstOrDefault();

            if (wjFolder == default)
            {
                LogLine("No Wabbajack install found to launch.");
                _errors.Add("No WJ install found");
                throw new Exception("No WJ install found");
            }

            var app = wjFolder.Combine(Platform.AppExecutable);
            try
            {
                await Platform.WriteCliShortcut(app);
            }
            catch (Exception ex)
            {
                _errors.Add($"Creating the CLI shortcut : {ex}");
            }

            Process.Start(Platform.StartApp(app, Environment.GetCommandLineArgs().Skip(1).ToArray()));
        }
        catch (Exception ex)
        {
            LogLine($"ERROR in FinishAndExit: {ex.Message}");
            _errors.Add(ex.Message);
            if (_errors.Count == 0)
            {
                Status = "Failed: Unknown error";
                await Task.Delay(10000);
            }

            foreach (var error in _errors)
            {
                Status = "Failed: " + error;
                await Task.Delay(10000);
            }
        }
        finally
        {
            LogLine(_errors.Count > 0
                ? $"Launcher exiting with {_errors.Count} error(s): {string.Join(" | ", _errors)}"
                : "Launcher exiting normally.");
            Environment.Exit(0);
        }
    }

    private void UpdateProgress(string what, object sender, DownloadProgressChangedEventArgs e)
    {
        Status = $"Downloading {what} ({e.ProgressPercentage}%)...";
    }

    private static ILauncherPlatform Platform => LauncherPlatform.Current;

    private async Task<(Version Version, long Size, Func<Task<Uri>> Uri)> GetGithubRelease(CancellationToken token)
    {
        var releases = await GetGithubReleases();
        var picked = Platform.PickGitHubRelease(releases.Select(r => new ReleaseInfo(r.Tag, r.Prerelease,
            r.Assets.Select(a => new ReleaseFile(a.Name, a.BrowserDownloadUrl, a.Size)).ToList())).ToList(), out var whyNot);
        if (picked == null)
        {
            Status = whyNot;
            return default;
        }

        var (version, file) = picked.Value;
        return (version, file.Size, async () => file.Url);
    }

    private async Task<Release[]> GetGithubReleases()
    {
        Status = "Checking GitHub Repository";
        var data = await _client.DownloadStringTaskAsync(GITHUB_REPO);
        Status = "Parsing Response";
        return JsonSerializer.Deserialize<Release[]>(data)!;
    }

    private async Task<(Version Version, long Size, Func<Task<Uri>> uri)> GetNexusReleases(CancellationToken token)
    {
        Status = "Checking Nexus for updates";
        if (!await _nexusApi.IsPremium(token))
            return default;

        var data = await _nexusApi.ModFiles("site", 403, token);
        Status = "Parsing Response";

        var picked = Platform.PickNexusFile(data.info.Files
            .Select(f => new NexusFile(f.Name, f.CategoryId, f.FileId, f.SizeInBytes ?? f.Size)).ToList());
        if (picked == null) return default;

        var (version, file) = picked.Value;
        return (version, file.Size, async () =>
        {
            var link = await _nexusApi.DownloadLink("site", 403, file.FileId, token);

            return link.info.First().URI;
        });
    }


    private partial class Release
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; }

        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }

        [JsonPropertyName("assets")] public Asset[] Assets { get; set; }
    }

    private partial class Asset
    {
        [JsonPropertyName("browser_download_url")]
        public Uri BrowserDownloadUrl { get; set; }

        [JsonPropertyName("name")] public string Name { get; set; }


        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
