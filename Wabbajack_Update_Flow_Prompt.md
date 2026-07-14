# Wabbajack Update Flow — Ask Permission Before Self-Update

## Task

Modify the Wabbajack application so that it always asks the user for permission before downloading or installing an application update.

Repository: `C:\CodePackages\wabbajack`

---

## Verified Architecture Anchors (pre-traced 2026-07-13 — verify, then build on these)

There are **TWO independent silent update paths** in this codebase. Both must be handled. Do not assume Wabbajack 2.x class names. Read the referenced files before changing anything.

### Update Path A — the Launcher (PRIMARY: this installs new app versions)

- Project: `Wabbajack.Launcher` — **this is an Avalonia app, NOT WPF.** It uses MsBox.Avalonia for message boxes (see `VerifyCurrentLocation()` for an existing example) and ReactiveUI.
- `Wabbajack.Launcher\ViewModels\MainWindowViewModel.cs`: the constructor unconditionally fires `CheckForUpdates()`, which:
  1. Checks Nexus (if premium token via `_tokenProvider.HaveToken()`), falling back to GitHub releases (`GetGithubRelease`).
  2. If the newest release's folder `{CurrentDirectory}\{version}\Wabbajack.exe` already exists locally → `FinishAndExit()` (no download).
  3. Otherwise it **silently downloads and extracts the new version zip** into `{CurrentDirectory}\{version}\`, then calls `FinishAndExit()`.
- `FinishAndExit()` always launches the **highest-versioned local folder** and forwards command-line args to it. Consequence: declining a download naturally launches the existing older version — no extra work needed for "continue running normally", **except** the fresh-install edge case below.
- **Insert the confirmation here**, between "newer release identified and no local copy of it exists" and the download. This is the main dialog required by this task.
- Dialog implementation: MsBox.Avalonia is already a dependency; use a message box with custom buttons ("Update now" / "Not now") or an equivalent Avalonia dialog. It must run on the Avalonia UI thread (`Dispatcher.UIThread`) and must not deadlock the async `CheckForUpdates()` flow.
- **Fresh-install edge case (must handle):** if NO local version folder exists yet, declining the download would leave nothing to launch. In that case, skip the confirmation entirely and download (this is an install, not an update), and log that the prompt was skipped for initial install.
- "Currently installed version" for the dialog = the highest version folder present in `KnownFolders.CurrentDirectory` (the same enumeration `FinishAndExit()` uses). "Available version" = `_version.Version`.

### Update Path B — LauncherUpdater inside the main WPF app

- `Wabbajack.App.Wpf\LauncherUpdater.cs`: silently downloads the newest `Wabbajack.exe` **launcher stub** from GitHub releases and replaces the parent-folder `Wabbajack.exe` when the release version is newer than the launcher's `FileVersionInfo`. It also deletes old version folders (keeps the 2 newest) — leave that cleanup logic alone.
- Invoked fire-and-forget in `Wabbajack.App.Wpf\Views\MainWindow.xaml.cs` constructor: `var _ = updater.Run();` (the `LauncherUpdater` is injected; registered as a singleton in `App.xaml.cs` `ConfigureServices`).
- **Decision (implement as specified, do not redesign):** gate this path behind the same `AskBeforeApplicationUpdate` setting, but do NOT show a second dialog for it. When the setting is `true`, use the existing confirmation dialog once, covering the launcher-stub replacement ("Update now" proceeds, "Not now" skips the replacement for this session). When the setting is `false`, preserve current silent behavior. Rationale: two stacked prompts (launcher stub + app version) in one session would be hostile UX; a single consent covers "application update".
- Existing WPF dialog infrastructure you should reuse: `MainWindowVM.ShowConfirmationDialog(string title, string message)` → `ConfirmationDialogVM` shown as `ActiveFloatingPane`. Note `LauncherUpdater` currently has no reference to `MainWindowVM`; wire the confirmation via a message (`MessageBus`, matching existing `Wabbajack.App.Wpf\Messages\` patterns) or by passing a confirmation callback — pick whichever matches existing conventions with the least architectural churn.

### Setting storage

- `Wabbajack.Configuration\MainSettings.cs` — shared settings class used by BOTH the launcher and the WPF app, persisted by `SettingsManager` (Wabbajack.Services.OSIntegrated) as `app_settings` JSON under `%localappdata%\Wabbajack\saved_settings\`. The launcher already loads it in `Wabbajack.Launcher\Program.cs` → `GetAppSettings(...)` and registers it as a DI singleton.
- Add `public bool AskBeforeApplicationUpdate { get; set; } = true;` to `MainSettings`. Because System.Text.Json leaves missing properties at their initializer default, existing settings files get `true` automatically — no `SettingsVersion` bump is strictly required, but follow the existing `Upgrade()` pattern if any migration logic is added.
- The WPF app side: check how `MainSettings` is resolved there (via `AddOSIntegrated()` / `SettingsManager`) and read the same instance; do not create a second storage location.

### Unattended / command-line behavior

- The launcher forwards all command-line args to the launched app (`FinishAndExit`). The WPF app has a CLI verb system (`CommandLineBuilder`, `Wabbajack.CLI`, `wabbajack-cli.exe`).
- No pre-existing "unattended update" flag was found during tracing, but verify with a repository-wide search before concluding. If none exists, add a launcher command-line flag (e.g. `--unattended-update` or match any existing arg-naming convention) that bypasses the dialog and logs the bypass. Do NOT make unattended the default; interactive runs must ask when the setting is `true`.

### Logging conventions

- Launcher: currently has no ILogger wired in `MainWindowViewModel` (uses `Status` strings and an `_errors` list; note `Program.cs` clears logging providers). Add log-equivalent entries via the existing `Status`/`StatusHistory` mechanism and, if a logger is cheaply available, ILogger too — do not build new logging infrastructure.
- WPF app: NLog via `ILogger<T>`, structured messages (see `LauncherUpdater` for style). Add entries for: update detected, accepted, declined, confirmation bypassed (unattended), skipped (fresh install), update failure.

---

## Requirements

1. Trace and confirm the two update paths above; correct this document's assumptions if the code has drifted.
2. When a newer Wabbajack version is detected in Path A (and a local version already exists), show a confirmation dialog BEFORE any download or install, displaying: currently installed version, available version, an **Update now** button, and a **Not now** button.
3. **Update now** → continue through the existing update process unchanged.
4. **Not now** → leave the installation unchanged, close the dialog, continue into `FinishAndExit()` so the current version launches normally, and do not re-show the dialog during the same session. (The launcher is a short-lived process that runs once per start, so once-per-session is inherent — verify no loop or retry re-triggers it.)
5. Gate Path B behind the same setting per the decision documented above.
6. Add persistent setting `AskBeforeApplicationUpdate` to `MainSettings`, default `true`. When `true`, confirmation is required; when `false`, current silent behavior is preserved.
7. Preserve unattended update support where it exists; add the explicit bypass flag if none exists. Bypass must be logged.
8. Do not silently change the default to automatic updating; interactive installations ask by default.
9. Dialog must be displayed on the correct UI thread (Avalonia `Dispatcher.UIThread` for Path A; WPF dispatcher / `RxApp.MainThreadScheduler` for Path B) and must not block or deadlock startup.
10. Extract the update decision logic (versions in hand + setting + unattended flag + fresh-install case → Proceed / Ask / Skip) into a small view-model-independent class so it is unit-testable without Avalonia or WPF.
11. Add or update tests covering:
    - no update available → no prompt, no download
    - update available, setting true, user accepts → proceed
    - update available, setting true, user declines → no download, current version launches
    - fresh install (no local version) → no prompt, download proceeds
    - unattended flag set → no prompt, proceed, bypass logged
    - setting false → no prompt, proceed (legacy behavior)
    - setting default value is `true` when absent from persisted JSON (round-trip through `SettingsManager` serialization or plain System.Text.Json)
    - Note: there is no existing test project for `Wabbajack.Launcher` or `Wabbajack.App.Wpf`. Put the decision logic where an existing `*.Test` xUnit project can reach it (e.g. `Wabbajack.Configuration` or a shared project), or create a new test project following the solution's naming conventions.
12. Build the affected projects (`dotnet build`) and correct all compilation errors. Run the tests.
13. Do not make unrelated formatting or architectural changes. Do not modify the old-version-folder cleanup in `LauncherUpdater` beyond gating the exe replacement.

## Deliverables

- Make the code changes directly in the repository.
- List every modified file.
- Explain the previous update flow (both paths).
- Explain where the confirmation was inserted in each path.
- Show the exact setting name and where it is stored on disk.
- Provide the build and test results.
- Mention any edge cases or parts of the update system that could not be tested locally (e.g. the live GitHub/Nexus release check, the actual exe replacement, and the launcher's process-restart behavior).
