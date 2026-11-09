using PhoneBox.Agent;
using Xunit;

public sealed class EndpointTests
{
    [Fact]
    public void DefaultsAndIsolatedInstancesHaveDistinctUrlsAndMutexes()
    {
        var standard = AgentEndpoint.FromArgs([]);
        var isolated = AgentEndpoint.FromArgs(["--config", "test.json", "--port", "18765"]);
        Assert.Equal(8765, standard.Port);
        Assert.Equal(@"Local\PhoneBox.Agent", standard.MutexName);
        Assert.Equal("http://127.0.0.1:18765/", isolated.SettingsUrl);
        Assert.NotEqual(standard.MutexName, isolated.MutexName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    [InlineData("8765; command")]
    public void InvalidPortsAreRejected(string value) => Assert.Throws<ArgumentException>(() => AgentEndpoint.FromArgs(["--port", value]));

    [Fact]
    public void MissingAndRepeatedPortsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => AgentEndpoint.FromArgs(["--port"]));
        Assert.Throws<ArgumentException>(() => AgentEndpoint.FromArgs(["--port", "18765", "--port", "18766"]));
    }

    [Fact]
    public void RestartPreservesTheConfigPathAndPortAsSeparateArguments()
    {
        var info = AppRestart.StartInfo("PhoneBoxAgent.exe", ["--config", "C:/test folder/config.json", "--port", "18765", "--restart"]);
        Assert.Equal(new[] { "--config", "C:/test folder/config.json", "--port", "18765", "--restart" }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        var hosted = AppRestart.StartInfo("dotnet", ["--port", "18765"]);
        Assert.EndsWith("PhoneBoxAgent.dll", hosted.ArgumentList[0]);
    }
}
