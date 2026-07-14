# Wabbajack UI Update — Downloaded Modlist Update Indicator

## Task

Modify the Wabbajack user interface so that downloaded `.wabbajack` modlist installers clearly indicate when a newer version of that modlist is available. The purpose is to let a user who keeps several downloaded modlist installers determine, from the Wabbajack UI, whether a newer installer should be downloaded and reviewed.

Repository: `C:\CodePackages\wabbajack`

---

## Verified Architecture Anchors (pre-traced — verify, then build on these)

The following was confirmed by direct inspection of this repository on 2026-07-13. Verify each anchor still holds before relying on it, but do NOT waste effort re-discovering the architecture from scratch, and do NOT invent Wabbajack 2.x class names (there is no `ModListMetadataVM` monolith, no `Wabbajack.Lib` project).

### Downloaded-file discovery and local metadata

- `Wabbajack.Services.OSIntegrated\Services\ModListDownloadMaintainer.cs` owns downloaded modlists.
  - `ModListPath(ModlistMetadata)` → `Configuration.ModListsDownloadLocation` combined with `metadata.NamespacedName.Replace("/", "_@@_")` + `.wabbajack` extension.
  - `DownloadModlist(...)` writes a **JSON sidecar** next to the archive: `path.WithExtension(Ext.MetaData)` containing the serialized `ModlistMetadata` **as of download time** — including `Version`. **This sidecar is the local-version source. Do not open or hash the large `.wabbajack` archive to obtain the local version.**
  - `HaveModList(...)` compares the file's xxHash64 (via `FileHashCache`) against the gallery's current `DownloadMetadata.Hash`. Consequence today: when the gallery publishes a newer version, a stale local download flips to `NotDownloaded` with no explanation. This is the UX gap being fixed.
- Fallback when a sidecar is missing (older downloads): the local version may be read from the modlist definition inside the `.wabbajack` archive if an existing cheap mechanism exists; otherwise the entry's update status is `Unknown`. Never guess.

### Gallery metadata and matching

- Gallery lists are fetched via `Client.LoadLists()` in `Wabbajack.Networking.WabbajackClientApi`, consumed by `ModListGalleryVM.LoadModLists()` and `HomeVM`.
- `Wabbajack.DTOs\ModList\ModListMetadata.cs`: class `ModlistMetadata`.
  - Stable matching key: `NamespacedName` = `$"{RepositoryName}/{Links.MachineURL}"`. The gallery `SourceCache` in `ModListGalleryVM` is already keyed on it. **Match on `NamespacedName`, never on `Title` or filename.**
  - `Version` is `System.Version?` (null when absent/malformed). Use `System.Version` comparison; treat null on either side as `Unknown`, not `Current`.
  - `DownloadMetadata.Hash` identifies the exact published artifact.

### View models and views for downloaded modlists

- `Wabbajack.App.Wpf\ViewModels\Gallery\BaseModListMetadataVM.cs` — contains enum `ModListStatus { NotDownloaded, Downloading, Downloaded }`, the `UpdateStatus()` method, `VersionText`, and `Location`. Natural extension point for the new update-status model.
- `Wabbajack.App.Wpf\ViewModels\Gallery\GalleryModListMetadataVM.cs`, `ModListGalleryVM.cs` — gallery tiles and filtering (DynamicData `SourceCache` + `Filter` chains, ReactiveUI.Fody `[Reactive]` properties).
- Views: `Wabbajack.App.Wpf\Views\ModListGalleryView.xaml`, `ModListTileView.xaml`, `ModListDetailsView.xaml`, plus `HomeView.xaml` (HomeVM shows `ModlistMetadata[]` from `LoadLists()`).
- There is **no dedicated "downloaded modlists" page** in this codebase. Surface the badge on gallery tiles (and details view where sensible) for entries whose local file exists.

### Known wrinkle — orphan downloads (must not break matching)

This fork's `ModListGalleryVM` contains protocol-handler code (`HandleDirectDownload`, `HandleNexusCollection`) that writes `.wabbajack` files into `downloaded_mod_lists` with arbitrary filenames (e.g. `{slug}_r{rev}.wabbajack`) and **no `.metadata` sidecar**, outside the `NamespacedName` naming convention. Such files must resolve to `Unknown` or `Unlisted`, never crash matching, and never be hidden or deleted.

### Async / threading conventions

- Follow existing patterns: `ReactiveCommand.CreateFromTask`, `FireAndForget()`, `LoadingLock`, `CancellationToken` plumbed through constructors, `RxApp.MainThreadScheduler` for UI-bound updates. The gallery check must not block the UI thread.

---

## Requirements

1. Inspect the current repository and confirm the anchors above; correct this document's assumptions if any have drifted.
2. For each downloaded `.wabbajack` file, determine: local modlist name, local version, matching key (`NamespacedName`), latest gallery version, and whether an update is available.
3. Add an update-status model with states equivalent to:
   - `Current`
   - `UpdateAvailable`
   - `Unknown`
   - `Unlisted`
   - `CheckFailed`
4. Mark `UpdateAvailable` only when BOTH: (a) local and online entries matched reliably via stable identifier, and (b) the online version is strictly newer than the local downloaded version.
5. Never mark `Current` when: version parsing fails, gallery metadata is unreachable, the modlist was removed from the gallery, or the identity match is uncertain.
6. Add a visible UI badge/flag per downloaded modlist entry, with wording similar to: `UPDATE AVAILABLE`, `CURRENT`, `STATUS UNKNOWN`, `NOT IN GALLERY`.
7. When an update is available, display both versions where practical (e.g. `Downloaded: 5.3.1` / `Available: 5.4.0`).
8. Add a tooltip or secondary message: "A newer modlist installer is available. Review the modlist changelog before reinstalling." Do not claim reinstallation is mandatory.
9. Preserve access to downloaded modlists no longer listed online — mark them `Unlisted`, never hide or delete.
10. Handle version formats safely; reuse `System.Version` semantics already used by `ModlistMetadata.Version`. Null/unparseable → `Unknown`.
11. Refresh status: when the gallery/downloaded view is opened, after gallery metadata refresh, after a new `.wabbajack` download completes, and on any existing manual-refresh command.
12. Do not re-read or re-hash large files on every UI redraw. Cache parsed local metadata keyed on (file path, last-write time, file size). Reading the small `.metadata` sidecar is cheap, but cache it anyway and invalidate on file change.
13. Use the project's existing async loading and cancellation patterns; the gallery check must never freeze the UI.
14. Add logging (ILogger, existing structured style) for: local metadata read, successful local-to-gallery match, update detected, unlisted modlist, invalid version, network/metadata failure.
15. Add or update tests covering:
    - local and online versions equal → `Current`
    - online newer than local → `UpdateAvailable`
    - local newer than online → NOT `UpdateAvailable` (define and document behavior; suggest `Current` or `Unknown`, not `UpdateAvailable`)
    - malformed version → `Unknown`
    - missing local version / missing sidecar → `Unknown`
    - missing gallery entry → `Unlisted`
    - duplicate or ambiguous names → no false match (identifier-based matching)
    - failed network/gallery lookup → `CheckFailed`
    - cached metadata invalidation after the file changes
    - Note: there is no existing `Wabbajack.App.Wpf` test project. Put the status/matching logic in a testable, view-model-independent class (e.g. in `Wabbajack.Services.OSIntegrated` or a shared project) and test it in an existing or new xUnit test project consistent with the solution's `*.Test` conventions.
16. Build the affected projects (`dotnet build` on the solution or affected projects) and correct all compilation errors. Run the new tests.
17. Do not make unrelated UI redesigns or broad refactoring changes.

## Deliverables

- Make the changes directly in the repository.
- List every modified file.
- Explain how local modlists are matched to gallery entries.
- Explain the version-comparison rules, including the local-newer-than-online case.
- Show the new status model and its UI binding.
- Provide build and test results.
- Include screenshots or a concise description of each visible status state.
- Search the repository history (`git log --oneline`, `git log -S UpdateAvailable`, etc.) for any older update-indicator implementation and state whether any of it was reused.
