using System.Diagnostics;
using PhoneBox.Agent;
using Xunit;

public sealed class ToolProcessTests
{
    private static ProcessStartInfo Shell(string windows, string unix)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh");
        foreach (var arg in OperatingSystem.IsWindows() ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", windows } : new[] { "-c", unix }) info.ArgumentList.Add(arg);
        return info;
    }

    [Fact]
    public async Task DrainsBothStreamsAndReportsFailure()
    {
        var result = await ToolProcess.RunAsync(Shell("[Console]::Out.Write('hello'); [Console]::Error.Write('failed'); exit 7", "printf hello; printf failed >&2; exit 7"), TimeSpan.FromSeconds(10), default);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("hello", result.Output);
        Assert.Equal("failed", result.Error);
    }

    [Fact]
    public async Task TimeoutTerminatesTheChildAndCancellationRemainsCancellation()
    {
        var marker = Path.Combine(Path.GetTempPath(), "pb-process-" + Guid.NewGuid());
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => ToolProcess.RunAsync(Shell(
                $"[IO.File]::WriteAllText('{marker}', [string]$PID); Start-Sleep -Seconds 60",
                $"echo $$ > '{marker}'; exec sleep 60"), TimeSpan.FromSeconds(3), default));
            Assert.True(File.Exists(marker), "The child must have started before timing out");
            var pid = int.Parse(File.ReadAllText(marker).Trim());
            var alive = false;
            try { using var child = Process.GetProcessById(pid); alive = !child.HasExited; } catch (ArgumentException) { }
            Assert.False(alive);
            using var ct = new CancellationTokenSource(); ct.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolProcess.RunAsync(Shell("Start-Sleep 60", "sleep 60"), TimeSpan.FromSeconds(10), ct.Token));
        }
        finally { File.Delete(marker); }
    }
}
