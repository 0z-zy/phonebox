using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PhoneBox.Agent;

public sealed record CommandResult(string Output, long At, bool Ok, bool Running, double DurationMs);

/// <summary>
/// Runs the PowerShell commands of "command" widgets. Commands live only in config.json on this PC; the phone
/// asks for a widget by page and widget id and gets the cached output. A command runs at most once per its
/// interval, and only while a phone is actually looking at it.
/// </summary>
public sealed class CommandRunner(ConfigStore cfg, ILogger<CommandRunner> log)
{
    private const int MaxOutput = 4000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, CommandResult> _results = new();
    private readonly ConcurrentDictionary<string, byte> _running = new();

    /// <returns>The cached result (possibly stale or empty while the first run is in progress), or null if there is no such widget.</returns>
    public CommandResult? Get(string pageId, string widgetId, bool force = false)
    {
        var w = cfg.Get().Pages.FirstOrDefault(p => p.Id == pageId)?.Widgets?.FirstOrDefault(x => x.Id == widgetId && x.Type == "command");
        if (w is null) return null;
        var command = w.Str("command") ?? "";
        var interval = Math.Clamp(w.Int("interval") ?? 30, 5, 86400);
        var key = $"{pageId}/{widgetId}:{command.GetHashCode()}";
        var cached = _results.GetValueOrDefault(key);
        var stale = cached is null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cached.At >= interval * 1000L;
        if ((stale || force) && command.Trim() != "" && _running.TryAdd(key, 0))
            _ = Task.Run(async () => { try { _results[key] = await RunAsync(command); } finally { _running.TryRemove(key, out _); } });
        var running = _running.ContainsKey(key);
        return cached is null
            ? new CommandResult(command.Trim() == "" ? "(no command)" : "", 0, true, running, 0)
            : cached with { Running = running };
    }

    private async Task<CommandResult> RunAsync(string command)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                         "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(Timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new CommandResult($"timed out after {Timeout.TotalSeconds:0} s", Now(), false, false, sw.Elapsed.TotalMilliseconds);
            }
            var output = (await stdout).TrimEnd();
            var err = (await stderr).Trim();
            var ok = p.ExitCode == 0;
            if (output == "" && err != "") output = err;
            if (output.Length > MaxOutput) output = output[..MaxOutput] + "…";
            return new CommandResult(output, Now(), ok, false, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            log.LogWarning("command widget failed: {Msg}", ex.Message);
            return new CommandResult(ex.Message, Now(), false, false, sw.Elapsed.TotalMilliseconds);
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
