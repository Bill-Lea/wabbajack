using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.Services.OSIntegrated.Services;

/// <summary>
///     Consolidates downloaded modlist installers that older Wabbajack versions stored in their own
///     per-version <c>downloaded_mod_lists</c> folders into a single version-independent shared folder
///     directly under <paramref name="commonFolder"/>.
///
///     Historically each version folder (<c>…\Wabbajack\4.2.1.4\downloaded_mod_lists</c>) kept its own
///     downloads, so a newer version could not see them and the launcher eventually deleted them along
///     with the old version folder. This pulls them up into <c>…\Wabbajack\downloaded_mod_lists</c> so
///     they persist across updates.
///
///     Kept free of any WPF dependency so it can be unit tested directly.
/// </summary>
public static class DownloadedModListConsolidator
{
    public const string DownloadsFolderName = "downloaded_mod_lists";

    /// <summary>
    ///     Moves per-version downloaded modlist files up into <c>commonFolder\downloaded_mod_lists</c>.
    ///     Newer versions' copies win on name conflicts; the operation is idempotent and best-effort
    ///     (individual failures are logged, never thrown). Returns the number of files moved.
    /// </summary>
    public static async Task<int> Consolidate(AbsolutePath commonFolder, ILogger logger,
        CancellationToken token = default)
    {
        var shared = commonFolder.Combine(DownloadsFolderName);
        try
        {
            if (!shared.DirectoryExists()) shared.CreateDirectory();

            // Per-version downloaded_mod_lists folders, newest version first so a newer copy of a
            // duplicate is never overwritten by an older one.
            var perVersionFolders = commonFolder
                .EnumerateDirectories(recursive: false)
                .Select(d => Version.TryParse(d.FileName.ToString(), out var ver) ? (ver, dir: d) : default)
                .Where(t => t != default)
                .OrderByDescending(t => t.ver)
                .Select(t => t.dir.Combine(DownloadsFolderName))
                .Where(d => d != shared && d.DirectoryExists())
                .ToArray();

            var moved = 0;
            foreach (var folder in perVersionFolders)
            {
                foreach (var file in folder.EnumerateFiles(recursive: false).ToArray())
                {
                    var target = shared.Combine(file.FileName);
                    if (target.FileExists()) continue; // a newer version already provided this file
                    try
                    {
                        await file.MoveToAsync(target, false, token);
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not consolidate downloaded modlist file {File}", file.FileName);
                    }
                }
            }

            if (moved > 0)
                logger.LogInformation("Consolidated {Count} downloaded modlist file(s) into shared folder {Folder}",
                    moved, shared);

            return moved;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to consolidate downloaded modlists into the shared folder");
            return 0;
        }
    }
}
