using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class StackConfigTests
{
    private static AgentConfig Config(string children) => JsonSerializer.Deserialize<AgentConfig>("""
        {"pages":[{"id":"stack","enabled":true,"widgets":[{"type":"stack","options":{"widgets":CHILDREN}}]}]}
        """.Replace("CHILDREN", children), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void NestedCommandsNeverReachThePhone()
    {
        var config = Config("""[{"type":"command","options":{"command":"PRIVATE SCRIPT"}},{"type":"clock"},{"type":"stack","options":{"widgets":[{"type":"command","options":{"command":"PRIVATE SCRIPT"}}]}}]""");
        var json = JsonSerializer.Serialize(config.ToPublic());
        Assert.DoesNotContain("PRIVATE SCRIPT", json);
        Assert.Single(config.Widgets(), w => w.Type == "clock");
        Assert.DoesNotContain(config.Widgets(), w => w.Type == "command");
    }

    [Fact]
    public void ChildPermissionsFollowEnabledStateAndPageVisibility()
    {
        var config = Config("""[{"type":"processes","enabled":false,"options":{"allowKill":true}}]""");
        Assert.False(config.AllowKill());
        config = Config("""[{"type":"processes","options":{"allowKill":true}}]""");
        Assert.True(config.AllowKill());
        config.Pages[0].Enabled = false;
        Assert.False(config.AllowKill());
    }

    [Fact]
    public void CodexBarSettingsPersistAndStayWithinSupportedRanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "pb-config-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new ConfigStore(path);
            Assert.Null(store.Save("""{"codexBar":{"providers":[],"pageSize":999,"rotateInterval":-1},"pages":[{"template":"codexbar"}]}"""));
            var config = new ConfigStore(path).Get();
            Assert.Empty(config.CodexBar.Providers);
            Assert.Equal(6, config.CodexBar.PageSize);
            Assert.Equal(2, config.CodexBar.RotateInterval);
            Assert.Contains(config.Pages[0].Widgets!, w => w.Type == "quotas");
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }
}
