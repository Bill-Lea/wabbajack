#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.Services.OSIntegrated.Services;
using Xunit;

namespace Wabbajack.DTOs.Test;

/// <summary>
///     Verifies that downloaded modlist installers stored in old per-version folders are consolidated into
///     the single shared folder, with newest-version copies winning on conflict.
/// </summary>
public class DownloadedModListConsolidatorTests : IDisposable
{
    private readonly AbsolutePath _common;

    public DownloadedModListConsolidatorTests()
    {
        _common = Path.Combine(Path.GetTempPath(), "wj_consolidate_" + Guid.NewGuid().ToString("N")).ToAbsolutePath();
        _common.CreateDirectory();
    }

    public void Dispose()
    {
        try { if (_common.DirectoryExists()) _common.DeleteDirectory(); } catch { /* best-effort */ }
    }

    private AbsolutePath VersionDownloads(string version)
    {
        var dir = _common.Combine(version, "downloaded_mod_lists");
        dir.CreateDirectory();
        return dir;
    }

    private static void Write(AbsolutePath folder, string name, string content)
        => folder.Combine(name).WriteAllText(content);

    private AbsolutePath Shared => _common.Combine("downloaded_mod_lists");

    [Fact]
    public async Task MovesFilesFromVersionFoldersIntoSharedFolder()
    {
        Write(VersionDownloads("4.2.1.4"), "repo_@@_list.wabbajack", "archive");
        Write(VersionDownloads("4.2.1.4"), "repo_@@_list.wabbajack.metadata", "{\"version\":\"1.0.0\"}");

        var moved = await DownloadedModListConsolidator.Consolidate(_common, NullLogger.Instance);

        Assert.Equal(2, moved);
        Assert.True(Shared.Combine("repo_@@_list.wabbajack").FileExists());
        Assert.True(Shared.Combine("repo_@@_list.wabbajack.metadata").FileExists());
        // originals moved out of the per-version folder
        Assert.False(_common.Combine("4.2.1.4", "downloaded_mod_lists", "repo_@@_list.wabbajack").FileExists());
    }

    [Fact]
    public async Task NewerVersionWinsOnConflict()
    {
        // Same file exists in two version folders with different sidecar contents.
        Write(VersionDownloads("4.1.0.1"), "repo_@@_list.wabbajack.metadata", "OLD");
        Write(VersionDownloads("4.2.1.4"), "repo_@@_list.wabbajack.metadata", "NEW");

        await DownloadedModListConsolidator.Consolidate(_common, NullLogger.Instance);

        Assert.Equal("NEW", Shared.Combine("repo_@@_list.wabbajack.metadata").ReadAllText());
    }

    [Fact]
    public async Task ConsolidatesAcrossMultipleOldVersionFolders()
    {
        Write(VersionDownloads("4.1.0.1"), "a_@@_1.wabbajack", "a");
        Write(VersionDownloads("4.2.0.0"), "b_@@_2.wabbajack", "b");
        Write(VersionDownloads("4.2.1.4"), "c_@@_3.wabbajack", "c");

        var moved = await DownloadedModListConsolidator.Consolidate(_common, NullLogger.Instance);

        Assert.Equal(3, moved);
        Assert.True(Shared.Combine("a_@@_1.wabbajack").FileExists());
        Assert.True(Shared.Combine("b_@@_2.wabbajack").FileExists());
        Assert.True(Shared.Combine("c_@@_3.wabbajack").FileExists());
    }

    [Fact]
    public async Task IsIdempotent_SecondRunMovesNothing()
    {
        Write(VersionDownloads("4.2.1.4"), "repo_@@_list.wabbajack", "archive");

        var first = await DownloadedModListConsolidator.Consolidate(_common, NullLogger.Instance);
        var second = await DownloadedModListConsolidator.Consolidate(_common, NullLogger.Instance);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.True(Shared.Combine("repo_@@_list.wabbajack").FileExists());
    }

    [Fact]
    public async Task IgnoresNonVersionFoldersAndTheSharedFolderItself()
    {
        // A pre-existing file already in the shared folder must be left untouched.
        Shared.CreateDirectory();
        Write(Shared, "already_@@_here.wabbajack", "keep");
        // A non-version-named folder must be ignored.
        var notAVersion = _common.Combine("cache", "downloaded_mod_lists");
        notAVersion.CreateDirectory();
        Write(notAVersion, "ignored_@@_x.wabbajack", "nope");

        var moved = await DownloadedModListConsolidator.Consolidate(_common, NullLogger.Instance);

        Assert.Equal(0, moved);
        Assert.True(Shared.Combine("already_@@_here.wabbajack").FileExists());
        Assert.True(notAVersion.Combine("ignored_@@_x.wabbajack").FileExists()); // untouched
    }
}
