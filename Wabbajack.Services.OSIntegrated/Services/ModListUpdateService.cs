using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.DTOs;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.Services.OSIntegrated.Services;

/// <summary>
///     Result of comparing a locally downloaded modlist installer against the gallery.
/// </summary>
public enum ModListUpdateState
{
    /// <summary>The downloaded version matches (or is newer than) the gallery version.</summary>
    Current,

    /// <summary>The gallery publishes a strictly newer version than the downloaded one.</summary>
    UpdateAvailable,

    /// <summary>Version information was missing or could not be parsed on either side.</summary>
    Unknown,

    /// <summary>The downloaded modlist could not be matched to any gallery entry.</summary>
    Unlisted,

    /// <summary>Reading local metadata or gallery metadata failed unexpectedly.</summary>
    CheckFailed
}

/// <summary>
///     Immutable result describing whether a downloaded modlist installer is up to date.
/// </summary>
public readonly record struct ModListUpdateResult(
    ModListUpdateState State,
    Version? LocalVersion,
    Version? OnlineVersion)
{
    public static ModListUpdateResult Failed { get; } =
        new(ModListUpdateState.CheckFailed, null, null);

    public bool IsUpdateAvailable => State == ModListUpdateState.UpdateAvailable;
}

/// <summary>
///     Determines whether a downloaded <c>.wabbajack</c> modlist installer has a newer version
///     available in the gallery.
///
///     The local version is read from the small JSON <c>.metadata</c> sidecar written next to the
///     archive at download time (see <see cref="ModListDownloadMaintainer.DownloadModlist"/>) — the
///     large archive is never opened or re-hashed here. Parsed sidecar metadata is cached keyed on
///     (path, last-write-time, size) so repeated UI redraws don't re-read the file, and the cache is
///     invalidated automatically when the file changes.
///
///     Modlists are matched to the gallery on the stable <see cref="ModlistMetadata.NamespacedName"/>
///     identifier only — never on title or filename — so lists that share a display name never
///     cross-match, and orphan downloads that follow no naming convention resolve to
///     <see cref="ModListUpdateState.Unlisted"/> rather than crashing.
/// </summary>
public class ModListUpdateService
{
    private readonly ILogger<ModListUpdateService> _logger;
    private readonly ConcurrentDictionary<AbsolutePath, CacheEntry> _localMetadataCache = new();

    public ModListUpdateService(ILogger<ModListUpdateService> logger)
    {
        _logger = logger;
    }

    private readonly record struct CacheEntry(DateTime LastWriteUtc, long Size, ModlistMetadata? Metadata);

    /// <summary>
    ///     Pure version-comparison rule, isolated from all I/O so it can be unit tested directly.
    ///
    ///     Rules:
    ///     <list type="bullet">
    ///         <item>Not matched to the gallery → <see cref="ModListUpdateState.Unlisted"/>.</item>
    ///         <item>Either version null/unparseable → <see cref="ModListUpdateState.Unknown"/>.</item>
    ///         <item>Online strictly greater than local → <see cref="ModListUpdateState.UpdateAvailable"/>.</item>
    ///         <item>Otherwise (equal, or local newer than online) → <see cref="ModListUpdateState.Current"/>.</item>
    ///     </list>
    ///
    ///     The local-newer-than-online case is deliberately reported as <see cref="ModListUpdateState.Current"/>
    ///     (no action needed), mirroring <c>ApplicationUpdateDecider</c>'s <see cref="System.Version"/>
    ///     semantics used for the application auto-updater. We never invent an "UpdateAvailable" when the
    ///     user already holds a newer or equal build.
    /// </summary>
    public static ModListUpdateState CompareVersions(Version? localVersion, Version? onlineVersion, bool matchedInGallery)
    {
        if (!matchedInGallery)
            return ModListUpdateState.Unlisted;

        if (localVersion is null || onlineVersion is null)
            return ModListUpdateState.Unknown;

        return onlineVersion.CompareTo(localVersion) > 0
            ? ModListUpdateState.UpdateAvailable
            : ModListUpdateState.Current;
    }

    /// <summary>
    ///     Reads (and caches) the <see cref="ModlistMetadata"/> from the <c>.metadata</c> sidecar next to
    ///     a downloaded <c>.wabbajack</c> archive. Returns <c>null</c> when no sidecar exists (older
    ///     downloads or orphan downloads) or when it cannot be parsed.
    /// </summary>
    public virtual async Task<ModlistMetadata?> ReadLocalMetadataAsync(AbsolutePath wabbajackArchive, CancellationToken token = default)
    {
        var sidecar = wabbajackArchive.WithExtension(Ext.MetaData);
        if (!sidecar.FileExists())
        {
            _logger.LogDebug("No local metadata sidecar for {Archive}", wabbajackArchive.FileName);
            _localMetadataCache.TryRemove(sidecar, out _);
            return null;
        }

        var lastWrite = sidecar.LastModifiedUtc();
        var size = sidecar.Size();

        if (_localMetadataCache.TryGetValue(sidecar, out var cached)
            && cached.LastWriteUtc == lastWrite && cached.Size == size)
        {
            return cached.Metadata;
        }

        ModlistMetadata? metadata = null;
        try
        {
            var text = await sidecar.ReadAllTextAsync();
            // Deserialize with the same plain System.Text.Json settings used to write the sidecar
            // in ModListDownloadMaintainer.DownloadModlist.
            metadata = JsonSerializer.Deserialize<ModlistMetadata>(text);
            _logger.LogInformation("Read local modlist metadata for {Name} (downloaded version {Version})",
                metadata?.NamespacedName ?? sidecar.FileName.ToString(), metadata?.Version?.ToString() ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read local modlist metadata sidecar {Sidecar}", sidecar);
        }

        _localMetadataCache[sidecar] = new CacheEntry(lastWrite, size, metadata);
        return metadata;
    }

    /// <summary>
    ///     Determines the update status of a single downloaded modlist installer given the gallery entry
    ///     it corresponds to. <paramref name="galleryMatch"/> is the online metadata (current published
    ///     version); pass <c>null</c> if the downloaded file has no matching gallery entry.
    /// </summary>
    public async Task<ModListUpdateResult> CheckForUpdateAsync(AbsolutePath wabbajackArchive,
        ModlistMetadata? galleryMatch, CancellationToken token = default)
    {
        try
        {
            var local = await ReadLocalMetadataAsync(wabbajackArchive, token);
            var localVersion = local?.Version;
            var onlineVersion = galleryMatch?.Version;
            var matched = galleryMatch is not null;

            var state = CompareVersions(localVersion, onlineVersion, matched);

            switch (state)
            {
                case ModListUpdateState.UpdateAvailable:
                    _logger.LogInformation(
                        "Update available for {Name}: downloaded {Local}, gallery {Online}",
                        galleryMatch!.NamespacedName, localVersion, onlineVersion);
                    break;
                case ModListUpdateState.Unlisted:
                    _logger.LogInformation("Downloaded modlist {Archive} is not listed in the gallery",
                        wabbajackArchive.FileName);
                    break;
                case ModListUpdateState.Unknown:
                    _logger.LogInformation(
                        "Update status unknown for {Name}: missing or invalid version (local {Local}, online {Online})",
                        galleryMatch?.NamespacedName ?? wabbajackArchive.FileName.ToString(),
                        localVersion?.ToString() ?? "none", onlineVersion?.ToString() ?? "none");
                    break;
                case ModListUpdateState.Current:
                    _logger.LogDebug("Downloaded modlist {Name} is current (version {Local})",
                        galleryMatch!.NamespacedName, localVersion);
                    break;
            }

            return new ModListUpdateResult(state, localVersion, onlineVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to determine update status for {Archive}", wabbajackArchive.FileName);
            return ModListUpdateResult.Failed;
        }
    }

    /// <summary>
    ///     Matches a locally downloaded modlist (via its sidecar metadata) against a gallery collection
    ///     keyed on <see cref="ModlistMetadata.NamespacedName"/>, then reports its update status. Used for
    ///     downloaded entries whose gallery entry must be discovered (e.g. to detect
    ///     <see cref="ModListUpdateState.Unlisted"/> lists removed from the gallery).
    /// </summary>
    public async Task<ModListUpdateResult> CheckForUpdateAsync(AbsolutePath wabbajackArchive,
        IReadOnlyDictionary<string, ModlistMetadata> galleryByNamespacedName, CancellationToken token = default)
    {
        try
        {
            var local = await ReadLocalMetadataAsync(wabbajackArchive, token);
            ModlistMetadata? galleryMatch = null;
            if (local is not null && !string.IsNullOrWhiteSpace(local.NamespacedName))
                galleryByNamespacedName.TryGetValue(local.NamespacedName, out galleryMatch);

            var state = CompareVersions(local?.Version, galleryMatch?.Version, galleryMatch is not null);
            return new ModListUpdateResult(state, local?.Version, galleryMatch?.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to determine update status for {Archive}", wabbajackArchive.FileName);
            return ModListUpdateResult.Failed;
        }
    }
}
