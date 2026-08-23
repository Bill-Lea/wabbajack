using System.Text.Json;
using Wabbajack.Configuration;
using Xunit;

namespace Wabbajack.Configuration.Test;

public class ApplicationUpdateDeciderTests
{
    private static readonly Version V1 = new(1, 0, 0, 0);
    private static readonly Version V2 = new(2, 0, 0, 0);

    [Fact]
    public void NoUpdateAvailable_WhenAvailableEqualsInstalled()
    {
        var decision = ApplicationUpdateDecider.Decide(V2, V2,
            askBeforeApplicationUpdate: true, unattended: false);

        Assert.Equal(UpdateAction.NoUpdate, decision.Action);
    }

    [Fact]
    public void NoUpdateAvailable_WhenAvailableOlderThanInstalled()
    {
        var decision = ApplicationUpdateDecider.Decide(V2, V1,
            askBeforeApplicationUpdate: true, unattended: false);

        Assert.Equal(UpdateAction.NoUpdate, decision.Action);
    }

    [Fact]
    public void UpdateAvailable_SettingTrue_AsksUser()
    {
        var decision = ApplicationUpdateDecider.Decide(V1, V2,
            askBeforeApplicationUpdate: true, unattended: false);

        Assert.Equal(UpdateAction.AskUser, decision.Action);
        Assert.Equal(UpdateBypassReason.None, decision.Reason);
    }

    [Fact]
    public void FreshInstall_NoInstalledVersion_ProceedsWithoutPrompt()
    {
        var decision = ApplicationUpdateDecider.Decide(null, V2,
            askBeforeApplicationUpdate: true, unattended: false);

        Assert.Equal(UpdateAction.UpdateWithoutPrompt, decision.Action);
        Assert.Equal(UpdateBypassReason.FreshInstall, decision.Reason);
    }

    [Fact]
    public void UnattendedFlag_ProceedsWithoutPrompt_AndReportsBypass()
    {
        var decision = ApplicationUpdateDecider.Decide(V1, V2,
            askBeforeApplicationUpdate: true, unattended: true);

        Assert.Equal(UpdateAction.UpdateWithoutPrompt, decision.Action);
        Assert.Equal(UpdateBypassReason.UnattendedFlag, decision.Reason);
    }

    [Fact]
    public void SettingFalse_ProceedsWithoutPrompt_LegacyBehavior()
    {
        var decision = ApplicationUpdateDecider.Decide(V1, V2,
            askBeforeApplicationUpdate: false, unattended: false);

        Assert.Equal(UpdateAction.UpdateWithoutPrompt, decision.Action);
        Assert.Equal(UpdateBypassReason.SettingDisabled, decision.Reason);
    }

    [Fact]
    public void FreshInstall_TakesPrecedenceOverSettingAndUnattended()
    {
        var decision = ApplicationUpdateDecider.Decide(null, V2,
            askBeforeApplicationUpdate: false, unattended: true);

        Assert.Equal(UpdateAction.UpdateWithoutPrompt, decision.Action);
        Assert.Equal(UpdateBypassReason.FreshInstall, decision.Reason);
    }

    [Fact]
    public void AskBeforeApplicationUpdate_DefaultsToTrue_WhenAbsentFromJson()
    {
        // A settings file written before the property existed has no "AskBeforeApplicationUpdate" key.
        const string legacyJson = "{\"CurrentSettingsVersion\":1}";

        var settings = JsonSerializer.Deserialize<MainSettings>(legacyJson);

        Assert.NotNull(settings);
        Assert.True(settings!.AskBeforeApplicationUpdate);
    }

    [Fact]
    public void AskBeforeApplicationUpdate_RoundTripsThroughJson()
    {
        var original = new MainSettings { AskBeforeApplicationUpdate = false };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<MainSettings>(json);

        Assert.NotNull(restored);
        Assert.False(restored!.AskBeforeApplicationUpdate);
    }
}
