namespace Wabbajack.Configuration;

public class MainSettings
{
    public const string SettingsFileName = "app_settings";
    private const int SettingsVersion = 1;

    public int CurrentSettingsVersion { get; set; }

    /// <summary>
    /// When true, the application asks the user for permission before downloading or installing
    /// an application update. When false, the legacy silent-update behavior is preserved.
    /// Defaults to true; because System.Text.Json leaves absent properties at this initializer
    /// value, existing settings files that predate this property deserialize as true.
    /// </summary>
    public bool AskBeforeApplicationUpdate { get; set; } = true;

    public bool Upgrade()
    {
        if (CurrentSettingsVersion == SettingsVersion)
        {
            return false;
        }

        CurrentSettingsVersion = SettingsVersion;
        return true;
    }
}