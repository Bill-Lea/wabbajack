#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Wabbajack.Common;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.Services.OSIntegrated.Services;
using Xunit;

namespace Wabbajack.DTOs.Test;

/// <summary>
///     Covers the view-model-independent update-status logic in
///     <see cref="ModListUpdateService"/>: pure version comparison plus sidecar reading, caching
///     and gallery matching against real temp files.
/// </summary>
public class ModListUpdateServiceTests : IDisposable
{
    private readonly AbsolutePath _tempDir;

    public ModListUpdateServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wj_update_tests_" + Guid.NewGuid().ToString("N"))
            .ToAbsolutePath();
        _tempDir.CreateDirectory();
    }

    public void Dispose()
    {
        try { if (_tempDir.DirectoryExists()) _tempDir.DeleteDirectory(); }
        catch { /* best-effort cleanup */ }
    }

    private static ModListUpdateService NewService() => new(NullLogger<ModListUpdateService>.Instance);

    private static ModlistMetadata Gallery(string ns, string? version)
    {
        var slash = ns.IndexOf('/');
        return new ModlistMetadata
        {
            RepositoryName = slash >= 0 ? ns[..slash] : ns,
            Links = new LinksObject { MachineURL = slash >= 0 ? ns[(slash + 1)..] : "" },
            Version = version is null ? null : Version.Parse(version)
        };
    }

    // Writes a downloaded .wabbajack archive path and its .metadata sidecar, returns the archive path.
    private AbsolutePath WriteDownload(string fileStem, string sidecarJson)
    {
        var archive = _tempDir.Combine(fileStem).WithExtension(Ext.Wabbajack);
        archive.WithExtension(Ext.MetaData).WriteAllText(sidecarJson);
        return archive;
    }

    private AbsolutePath WriteDownload(string fileStem, ModlistMetadata sidecar)
        // Serialize exactly as ModListDownloadMaintainer.DownloadModlist does (plain System.Text.Json).
        => WriteDownload(fileStem, JsonSerializer.Serialize(sidecar));

    // ---- Pure comparison rules -------------------------------------------------

    [Fact]
    public void EqualVersions_AreCurrent()
    {
        Assert.Equal(ModListUpdateState.Current,
            ModListUpdateService.CompareVersions(Version.Parse("5.3.1"), Version.Parse("5.3.1"), matchedInGallery: true));
    }

    [Fact]
    public void OnlineNewer_IsUpdateAvailable()
    {
        Assert.Equal(ModListUpdateState.UpdateAvailable,
            ModListUpdateService.CompareVersions(Version.Parse("5.3.1"), Version.Parse("5.4.0"), matchedInGallery: true));
    }

    [Fact]
    public void LocalNewerThanOnline_IsCurrent_NotUpdateAvailable()
    {
        // Documented behavior: holding a newer build than the gallery is "Current" (no action), never UpdateAvailable.
        var state = ModListUpdateService.CompareVersions(Version.Parse("5.5.0"), Version.Parse("5.4.0"), matchedInGallery: true);
        Assert.Equal(ModListUpdateState.Current, state);
        Assert.NotEqual(ModListUpdateState.UpdateAvailable, state);
    }

    [Fact]
    public void NullVersionEitherSide_IsUnknown()
    {
        Assert.Equal(ModListUpdateState.Unknown,
            ModListUpdateService.CompareVersions(null, Version.Parse("5.4.0"), matchedInGallery: true));
        Assert.Equal(ModListUpdateState.Unknown,
            ModListUpdateService.CompareVersions(Version.Parse("5.4.0"), null, matchedInGallery: true));
    }

    [Fact]
    public void NotMatched_IsUnlisted()
    {
        Assert.Equal(ModListUpdateState.Unlisted,
            ModListUpdateService.CompareVersions(Version.Parse("5.4.0"), Version.Parse("5.4.0"), matchedInGallery: false));
    }

    // ---- Sidecar-backed status -------------------------------------------------

    [Fact]
    public async Task LocalEqualsOnline_ReportsCurrent()
    {
        var svc = NewService();
        var archive = WriteDownload("repo_@@_list", Gallery("repo/list", "5.3.1"));
        var result = await svc.CheckForUpdateAsync(archive, Gallery("repo/list", "5.3.1"));
        Assert.Equal(ModListUpdateState.Current, result.State);
    }

    [Fact]
    public async Task OnlineNewer_ReportsUpdateAvailable_WithBothVersions()
    {
        var svc = NewService();
        var archive = WriteDownload("repo_@@_list", Gallery("repo/list", "5.3.1"));
        var result = await svc.CheckForUpdateAsync(archive, Gallery("repo/list", "5.4.0"));
        Assert.Equal(ModListUpdateState.UpdateAvailable, result.State);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(Version.Parse("5.3.1"), result.LocalVersion);
        Assert.Equal(Version.Parse("5.4.0"), result.OnlineVersion);
    }

    [Fact]
    public async Task MalformedLocalVersion_ReportsUnknown()
    {
        var svc = NewService();
        // Invalid version string makes the whole sidecar unparseable -> treated as no local version.
        var archive = WriteDownload("repo_@@_list",
            "{\"repositoryName\":\"repo\",\"links\":{\"machineURL\":\"list\"},\"version\":\"not.a.version\"}");
        var result = await svc.CheckForUpdateAsync(archive, Gallery("repo/list", "5.4.0"));
        Assert.Equal(ModListUpdateState.Unknown, result.State);
    }

    [Fact]
    public async Task MissingSidecar_ReportsUnknown()
    {
        var svc = NewService();
        // Archive path with no .metadata sidecar written next to it.
        var archive = _tempDir.Combine("repo_@@_orphan").WithExtension(Ext.Wabbajack);
        var result = await svc.CheckForUpdateAsync(archive, Gallery("repo/orphan", "5.4.0"));
        Assert.Equal(ModListUpdateState.Unknown, result.State);
    }

    [Fact]
    public async Task NoGalleryMatch_ReportsUnlisted()
    {
        var svc = NewService();
        var archive = WriteDownload("repo_@@_gone", Gallery("repo/gone", "5.4.0"));

        // VM-style overload: no gallery entry supplied.
        var byParam = await svc.CheckForUpdateAsync(archive, galleryMatch: null);
        Assert.Equal(ModListUpdateState.Unlisted, byParam.State);

        // Dictionary overload: sidecar's NamespacedName is absent from the gallery.
        var gallery = new Dictionary<string, ModlistMetadata> { ["repo/other"] = Gallery("repo/other", "9.9.9") };
        var byLookup = await svc.CheckForUpdateAsync(archive, gallery);
        Assert.Equal(ModListUpdateState.Unlisted, byLookup.State);
    }

    [Fact]
    public async Task AmbiguousTitles_MatchOnNamespacedNameOnly()
    {
        var svc = NewService();
        // Two gallery lists share a display concept but have distinct NamespacedNames.
        var gallery = new Dictionary<string, ModlistMetadata>
        {
            ["repoA/skyrim"] = Gallery("repoA/skyrim", "2.0.0"),
            ["repoB/skyrim"] = Gallery("repoB/skyrim", "9.0.0"),
        };

        // Downloaded list belongs to repoA and must not pick up repoB's newer version.
        var archive = WriteDownload("repoA_@@_skyrim", Gallery("repoA/skyrim", "2.0.0"));
        var result = await svc.CheckForUpdateAsync(archive, gallery);
        Assert.Equal(ModListUpdateState.Current, result.State);
        Assert.Equal(Version.Parse("2.0.0"), result.OnlineVersion);
    }

    [Fact]
    public async Task FailedLookup_ReportsCheckFailed()
    {
        var svc = new ThrowingService();
        var archive = WriteDownload("repo_@@_list", Gallery("repo/list", "5.3.1"));
        var result = await svc.CheckForUpdateAsync(archive, Gallery("repo/list", "5.4.0"));
        Assert.Equal(ModListUpdateState.CheckFailed, result.State);
    }

    [Fact]
    public async Task CachedMetadata_InvalidatedAfterFileChanges()
    {
        var svc = NewService();
        var archive = WriteDownload("repo_@@_list", Gallery("repo/list", "5.3.1"));

        var first = await svc.CheckForUpdateAsync(archive, Gallery("repo/list", "5.4.0"));
        Assert.Equal(ModListUpdateState.UpdateAvailable, first.State);
        Assert.Equal(Version.Parse("5.3.1"), first.LocalVersion);

        // Simulate the user downloading the newer installer: rewrite the sidecar with a bumped version.
        // Force a distinct last-write timestamp so the (path, last-write, size) cache key changes.
        var sidecar = archive.WithExtension(Ext.MetaData);
        sidecar.WriteAllText(JsonSerializer.Serialize(Gallery("repo/list", "5.4.0")));
        File.SetLastWriteTimeUtc(sidecar.ToString(), DateTime.UtcNow.AddSeconds(5));

        var second = await svc.CheckForUpdateAsync(archive, Gallery("repo/list", "5.4.0"));
        Assert.Equal(ModListUpdateState.Current, second.State);
        Assert.Equal(Version.Parse("5.4.0"), second.LocalVersion);
    }

    // Subclass that forces the exception path to verify it maps to CheckFailed.
    private sealed class ThrowingService : ModListUpdateService
    {
        public ThrowingService() : base(NullLogger<ModListUpdateService>.Instance) { }

        public override Task<ModlistMetadata?> ReadLocalMetadataAsync(AbsolutePath wabbajackArchive, CancellationToken token = default)
            => throw new IOException("simulated read failure");
    }
}
