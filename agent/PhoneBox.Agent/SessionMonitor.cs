using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <param name="Status">busy | waiting | shell | idle</param>
public sealed record AgentSession(string Id, string Tool, string Name, string Project, string Status,
    string? WaitingFor, long Since, long StartedAt, int? Pid = null);

/// <summary>
/// Watches running AI coding sessions (local and read-only):
///  - Claude Code: ~/.claude/sessions/{pid}.json (status files written by Claude Code itself)
///  - Codex: the latest events in recent rollout files under ~/.codex/sessions
/// </summary>
public sealed class SessionMonitor(ILogger<SessionMonitor> log) : BackgroundService
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly ConcurrentDictionary<string, string> _codexCwd = new();
    private volatile IReadOnlyList<AgentSession> _sessions = [];

    public IReadOnlyList<AgentSession> Snapshot() => _sessions;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var list = new List<AgentSession>();
            try { list.AddRange(ReadClaude()); } catch (Exception ex) { log.LogDebug("claude sessions: {Msg}", ex.Message); }
            try { list.AddRange(ReadCodex()); } catch (Exception ex) { log.LogDebug("codex sessions: {Msg}", ex.Message); }
            _sessions = list;
            await Task.Delay(2000, ct);
        }
    }

    private static IEnumerable<AgentSession> ReadClaude()
    {
        var dir = Path.Combine(Home, ".claude", "sessions");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            AgentSession? s = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pi) ? pi : 0;
                if (pid == 0 || !Alive(pid)) continue;
                var cwd = Str(r, "cwd") ?? "";
                s = new AgentSession(
                    Id: "claude:" + (Str(r, "sessionId") ?? pid.ToString()),
                    Tool: "claude",
                    Name: Str(r, "name") ?? Path.GetFileName(cwd.TrimEnd('\\', '/')),
                    Project: Path.GetFileName(cwd.TrimEnd('\\', '/')),
                    Status: Str(r, "status") ?? "idle",
                    WaitingFor: Str(r, "waitingFor"),
                    Since: Long(r, "statusUpdatedAt") ?? Long(r, "updatedAt") ?? 0,
                    StartedAt: Long(r, "startedAt") ?? 0,
                    Pid: pid);
            }
            catch { /* file was being written: retry on the next pass */ }
            if (s is not null) yield return s;
        }
    }

    private IEnumerable<AgentSession> ReadCodex()
    {
        var root = Path.Combine(Home, ".codex", "sessions");
        if (!Directory.Exists(root)) yield break;
        var cutoff = DateTime.UtcNow.AddHours(-3);
        var files = new List<FileInfo>();
        for (var d = 0; d < 10; d++)
        {
            var day = DateTime.Now.AddDays(-d);
            var dir = Path.Combine(root, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
            if (Directory.Exists(dir))
                files.AddRange(new DirectoryInfo(dir).GetFiles("rollout-*.jsonl").Where(f => f.LastWriteTimeUtc > cutoff));
        }

        foreach (var f in files)
        {
            AgentSession? s = null;
            try
            {
                var cwd = _codexCwd.GetOrAdd(f.FullName, ReadCodexCwd);
                var (last, lastTs) = LastCodexEvent(f.FullName);
                var age = DateTime.UtcNow - f.LastWriteTimeUtc;
                var status = last switch
                {
                    "task_complete" or "turn_aborted" => "idle",
                    _ when age < TimeSpan.FromMinutes(3) => "busy",
                    _ => "idle",
                };
                var project = Path.GetFileName(cwd.TrimEnd('\\', '/'));
                var id = f.Name.Length > 8 ? f.Name[^14..^6] : f.Name;
                s = new AgentSession("codex:" + f.Name, "codex", $"{project.ToLowerInvariant()}-{id[..4]}", project,
                    status, null, lastTs ?? new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    new DateTimeOffset(f.CreationTimeUtc).ToUnixTimeMilliseconds());
            }
            catch { }
            if (s is not null) yield return s;
        }
    }

    private static string ReadCodexCwd(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var first = sr.ReadLine();
        if (first is null) return "";
        using var doc = JsonDocument.Parse(first);
        return doc.RootElement.TryGetProperty("payload", out var p) ? Str(p, "cwd") ?? "" : "";
    }

    /// <summary>Returns the type and timestamp of the last "event_msg" event.</summary>
    private static (string? type, long? ts) LastCodexEvent(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = Math.Min(fs.Length, 256 * 1024);
        fs.Seek(-len, SeekOrigin.End);
        var buf = new byte[len];
        fs.ReadExactly(buf);
        var lines = Encoding.UTF8.GetString(buf).Split('\n');
        for (var i = lines.Length - 1; i >= 1; i--)
        {
            var l = lines[i];
            if (!l.Contains("\"event_msg\"") || l.Contains("\"token_count\"") || l.Contains("\"item_completed\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(l);
                var type = doc.RootElement.GetProperty("payload").TryGetProperty("type", out var t) ? t.GetString() : null;
                long? ts = DateTimeOffset.TryParse(Str(doc.RootElement, "timestamp"), out var dt) ? dt.ToUnixTimeMilliseconds() : null;
                return (type, ts);
            }
            catch { }
        }
        return (null, null);
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Long(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.TryGetInt64(out var l) ? l : null;
}
