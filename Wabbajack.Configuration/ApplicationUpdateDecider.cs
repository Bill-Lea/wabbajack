namespace Wabbajack.Configuration;

/// <summary>
/// What the caller should do about an available application update.
/// </summary>
public enum UpdateAction
{
    /// <summary>No newer version is available; nothing should be downloaded or installed.</summary>
    NoUpdate,

    /// <summary>A newer version is available and should be applied without prompting the user.</summary>
    UpdateWithoutPrompt,

    /// <summary>A newer version is available; the user must be asked before it is applied.</summary>
    AskUser
}

/// <summary>
/// Why a prompt was skipped when the action is <see cref="UpdateAction.UpdateWithoutPrompt"/>.
/// Purely informational, intended for logging.
/// </summary>
public enum UpdateBypassReason
{
    None,
    FreshInstall,
    UnattendedFlag,
    SettingDisabled
}

public readonly record struct UpdateDecision(UpdateAction Action, UpdateBypassReason Reason);

/// <summary>
/// Pure, framework-independent decision logic for whether an application update should proceed
/// silently, prompt the user, or be skipped. Kept free of any Avalonia/WPF dependency so it can be
/// unit tested directly.
/// </summary>
public static class ApplicationUpdateDecider
{
    /// <summary>
    /// Decide what to do given the versions in hand, the user's setting, and the unattended flag.
    /// </summary>
    /// <param name="installedVersion">
    /// The highest version currently installed locally, or null when nothing is installed yet
    /// (a fresh install).
    /// </param>
    /// <param name="availableVersion">The newest version discovered from the release source.</param>
    /// <param name="askBeforeApplicationUpdate">The persisted AskBeforeApplicationUpdate setting.</param>
    /// <param name="unattended">True when an unattended/command-line bypass was requested.</param>
    public static UpdateDecision Decide(Version? installedVersion, Version availableVersion,
        bool askBeforeApplicationUpdate, bool unattended)
    {
        // Nothing newer to install.
        if (installedVersion != null && availableVersion <= installedVersion)
            return new UpdateDecision(UpdateAction.NoUpdate, UpdateBypassReason.None);

        // Fresh install: there is no existing version to fall back to, so declining would leave
        // nothing to launch. Never block the initial install on a prompt.
        if (installedVersion == null)
            return new UpdateDecision(UpdateAction.UpdateWithoutPrompt, UpdateBypassReason.FreshInstall);

        // Explicit unattended bypass takes precedence over the interactive setting.
        if (unattended)
            return new UpdateDecision(UpdateAction.UpdateWithoutPrompt, UpdateBypassReason.UnattendedFlag);

        // Legacy silent behavior when the user has opted out of prompts.
        if (!askBeforeApplicationUpdate)
            return new UpdateDecision(UpdateAction.UpdateWithoutPrompt, UpdateBypassReason.SettingDisabled);

        return new UpdateDecision(UpdateAction.AskUser, UpdateBypassReason.None);
    }
}
