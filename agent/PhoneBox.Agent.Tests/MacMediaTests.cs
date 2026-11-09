using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class MacMediaTests
{
    [Fact]
    public void SystemMetadataAdvancesFromTheReportedPositionAndStopsWhenPaused()
    {
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"test.player","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(10000, monitor.ReadMacSystemMedia(json, now).Item1!.PosMs);
        Assert.Equal(12000, monitor.ReadMacSystemMedia(json, now.AddSeconds(2)).Item1!.PosMs);
        var paused = monitor.ReadMacSystemMedia(json.Replace("PlaybackRate\":1", "PlaybackRate\":0"), now.AddSeconds(4)).Item1!;
        Assert.False(paused.Playing);
        Assert.Equal(12000, paused.PosMs);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"kMRMediaRemoteNowPlayingInfoTitle\":42}")]
    [InlineData("not json")]
    public void MissingOrMalformedMetadataIsUnavailable(string json) =>
        Assert.Null(new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(json).Item1);
}
