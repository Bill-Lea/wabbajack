using System.Text.Json;
using Wabbajack.Configuration;
using Xunit;

namespace Wabbajack.Configuration.Test;

public class ApplicationUpdateDeciderTests
{
    private static readonly Version V1 = new(1,0,0,0);
    private static readonly Version V2 = new(2,0,0,0);

    [Fact]
    public void EqualVersion_NoUpdate()
    {
        var d=ApplicationUpdateDecider.Decide(V2,V2,true,false);
        Assert.Equal(UpdateAction.NoUpdate,d.Action);
    }

    [Fact]
    public void OlderAvailableVersion_NoUpdate()
    {
        var d=ApplicationUpdateDecider.Decide(V2,V1,true,false);
        Assert.Equal(UpdateAction.NoUpdate,d.Action);
    }

    [Fact]
    public void NewerVersion_AsksUser()
    {
        var d=ApplicationUpdateDecider.Decide(V1,V2,true,false);
        Assert.Equal(UpdateAction.AskUser,d.Action);
        Assert.Equal(UpdateBypassReason.None,d.Reason);
    }

    [Fact]
    public void SettingFalse_StillAsksUser()
    {
        var d=ApplicationUpdateDecider.Decide(V1,V2,false,false);
        Assert.Equal(UpdateAction.AskUser,d.Action);
    }

    [Fact]
    public void Unattended_StillAsksUser()
    {
        var d=ApplicationUpdateDecider.Decide(V1,V2,true,true);
        Assert.Equal(UpdateAction.AskUser,d.Action);
    }

    [Fact]
    public void SettingFalseAndUnattended_StillAsksUser()
    {
        var d=ApplicationUpdateDecider.Decide(V1,V2,false,true);
        Assert.Equal(UpdateAction.AskUser,d.Action);
    }

    [Fact]
    public void FreshInstall_ProceedsAutomatically()
    {
        var d=ApplicationUpdateDecider.Decide(null,V2,false,true);
        Assert.Equal(UpdateAction.UpdateWithoutPrompt,d.Action);
        Assert.Equal(UpdateBypassReason.FreshInstall,d.Reason);
    }

    [Fact]
    public void AskBeforeApplicationUpdate_DefaultsTrue()
    {
        const string json="{\"CurrentSettingsVersion\":1}";
        var s=JsonSerializer.Deserialize<MainSettings>(json);
        Assert.NotNull(s);
        Assert.True(s!.AskBeforeApplicationUpdate);
    }
}
