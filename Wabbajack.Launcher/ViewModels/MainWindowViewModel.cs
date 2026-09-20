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
using Wabbajack.Paths.IO;
#pragma warning disable SYSLIB0014

namespace Wabbajack.Launcher.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly WebClient _client = new();
    private readonly List<string> _errors = new();

    private (Version Version, long Size, Func<Task<Uri>> Uri) _version;
    public Uri GITHUB_REPO = new("https://api.github.com/repos/wabbajack-tools/wabbajack/releases");
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

        var baseFolder = KnownFolders.CurrentDirectory.Combine(_version.Version.ToString());

        if (baseFolder.Combine("Wabbajack.exe").FileExists())
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

        /*
        var archive = new Archive()
        {
            Name = $"{_version.Version}.zip",
            Size = _version.Size,
            State = new Http {Url = uri}
        };

        await using var stream = await _downloader.GetChunkedSeekableStream(archive, CancellationToken.None);
        var rdr = new ZipReader(stream, true);
        var entries = (await rdr.GetFiles()).OrderBy(d => d.FileOffset).ToArray();
        foreach (var file in  entries)
        {
            if (file.FileName.EndsWith("/") || file.FileName.EndsWith("\\")) continue;
            var relPath = file.FileName.ToRelativePath();
            Status = $"Extracting: {relPath.FileName}";
            var outPath = baseFolder.Combine(relPath);
            if (!outPath.Parent.DirectoryExists())
                outPath.Parent.CreateDirectory();

            await using var of = outPath.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            await rdr.Extract(file, of, CancellationToken.None);
        }*/


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
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                Status = $"Extracting: {entry.Name}";
                var outPath = baseFolder.Combine(entry.FullName.ToRelativePath());
                if (!outPath.Parent.DirectoryExists())
                    outPath.Parent.CreateDirectory();

                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    continue;
                await using var o = entry.Open();
                await using var of = outPath.Open(FileMode.Create, FileAccess.Write, FileShare.None);
                await o.CopyToAsync(of);
            }
        }
        catch (Exception ex)
        {
            _errors.Add(ex.Message);
        }

        try
        {
            await InstallWebView();
        }
        catch (Exception e)
        {
            _errors.Add(e.Message);
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

    [UriString]
    private const string WebViewDownloadLink = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private async Task InstallWebView(CancellationToken cancellationToken = default)
    {
        var setupPath = KnownFolders.WabbajackAppLocal.Combine("MicrosoftEdgeWebview2Setup.exe");
        if (setupPath.FileExists()) return;

        var wc = new WebClient();
        wc.DownloadProgressChanged += (sender, args) => UpdateProgress("WebView2", sender, args);

        Status = "Downloading WebView2 Runtime";

        byte[] data;
        try
        {
            data = await wc.DownloadDataTaskAsync(WebViewDownloadLink);
        }
        catch (Exception ex)
        {
            _errors.Add(ex.Message);
            await FinishAndExit();
            throw;
        }

        await setupPath.WriteAllBytesAsync(new Memory<byte>(data), cancellationToken);

        var process = new ProcessHelper
        {
            Path = setupPath,
            Arguments = new []{"/silent /install"}
        };

        await process.Start();
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
            var wjFolder = KnownFolders.CurrentDirectory.EnumerateDirectories(recursive:false)
                .OrderByDescending(v =>
                    Version.TryParse(v.FileName.ToString(), out var ver) ? ver : new Version(0, 0, 0, 0))
                .FirstOrDefault();

            if (wjFolder == default)
            {
                LogLine("No Wabbajack install found to launch.");
                _errors.Add("No WJ install found");
                throw new Exception("No WJ install found");
            }

            var filename = wjFolder.Combine("Wabbajack.exe");
            await CreateBatchFile(filename);
            LogLine($"Launching {filename}");
            var info = new ProcessStartInfo
            {
                FileName = filename.ToString(),
                Arguments = string.Join(" ",
                    Environment.GetCommandLineArgs().Skip(1).Select(s => s.Contains(' ') ? '\"' + s + '\"' : s)),
                WorkingDirectory = wjFolder.ToString()
            };
            Process.Start(info);
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

    private async Task CreateBatchFile(AbsolutePath filename)
    {
        try
        {
            filename = filename.Parent.Combine("cli", "wabbajack-cli.exe");
            var data = $"\"{filename}\" %*";
            var file = Path.Combine(Directory.GetCurrentDirectory(), "wabbajack-cli.bat");
            if (File.Exists(file) && await File.ReadAllTextAsync(file) == data) return;
            var parent = Directory.GetParent(file)!.FullName;
            if (!Directory.Exists(file))
                Directory.CreateDirectory(parent);
            await File.WriteAllTextAsync(file, data);
        }
        catch (Exception ex)
        {
            _errors.Add($"Creating Batch File : {ex}");
        }
    }

    private void UpdateProgress(string what, object sender, DownloadProgressChangedEventArgs e)
    {
        Status = $"Downloading {what} ({e.ProgressPercentage}%)...";
    }

    private async Task<(Version Version, long Size, Func<Task<Uri>> Uri)> GetGithubRelease(CancellationToken token)
    {
        var releases = await GetGithubReleases();


        var version = releases.Select(r =>
        {
            if (r.Tag.Split(".").Length == 4 && Version.TryParse(r.Tag, out var v))
                return (v, r);
            return (new Version(0, 0, 0, 0), r);
        })
            .OrderByDescending(r => r.Item1)
            .FirstOrDefault();

        var asset = version.r.Assets.FirstOrDefault(a => a.Name == version.Item1 + ".zip");
        if (asset == null)
        {
            Status = $"Error, no asset found for Github Release {version.r}";
            return default;
        }

        return (version.Item1, asset.Size, async () => asset!.BrowserDownloadUrl);
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
        //return JsonSerializer.Deserialize<Release[]>(data)!;

        var found = data.info.Files.Where(f => f.CategoryId == 5 || f.CategoryId == 3)
            .Where(f => f.Name.EndsWith(".zip"))
            .Select(f => Version.TryParse(f.Name[..^4], out var version) ? (version, f.SizeInBytes ?? f.Size,  f.FileId) : default)
            .FirstOrDefault(f => f != default);
        if (found == default) return default;

        return (found.version, found.Item2, async () =>
        {
            var link = await _nexusApi.DownloadLink("site", 403, found.FileId, token);

            return link.info.First().URI;
        });
    }


    private partial class Release
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; }

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
