using System.Diagnostics;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Automation rules (Settings › Rules): checks every rule once a second and, when its condition starts to hold, switches
/// the phone to a page, shows a notification or pushes a value. Rules marked "while active" undo their effect when the
/// condition ends: the phone goes back to the page it was on and a pushed value is removed again.
/// </summary>
public sealed class RuleEngine(ConfigStore config, PushStore push, SessionMonitor sessions, MediaMonitor media, SystemMonitor system,
    PrivacyMonitor privacy, ILogger<RuleEngine> log) : BackgroundService
{
    /// <summary>CPU spikes are normal; the CPU rule only fires when the load lasts this long.</summary>
    private const int CpuSustainSec = 15;

    private sealed class RuleState(RuleConfig rule)
    {
        public RuleConfig Rule { get; } = rule;
        public bool Active;
        public long TrueSince;
    }

    // keyed by the rule's serialized form, so editing a rule ends the old version and starts the new one
    private readonly Dictionary<string, RuleState> _states = [];
    private volatile string[] _active = [];
    private HashSet<string>? _procs;
    private long _procsAt;
    private double _idle;
    private long _idleAt;

    /// <summary>Ids of the rules whose condition holds right now (the phone keeps a rule's page only while it is listed).</summary>
    public IReadOnlyList<string> Active() => _active;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(3000, ct); // let the monitors take their first readings
        while (!ct.IsCancellationRequested)
        {
            try { Evaluate(); }
            catch (Exception ex) { log.LogWarning("rules: {Msg}", ex.Message); }
            await Task.Delay(1000, ct);
        }
    }

    private void Evaluate()
    {
        var rules = config.Get().Rules.Where(r => r.Enabled).ToList();
        var keys = rules.ToDictionary(r => JsonSerializer.Serialize(r), r => r);

        foreach (var (key, st) in _states.Where(kv => !keys.ContainsKey(kv.Key)).ToList())
        {
            if (st.Active && st.Rule.WhileActive) Undo(st.Rule);
            _states.Remove(key);
        }

        var now = Environment.TickCount64;
        var active = new List<string>();
        foreach (var (key, rule) in keys)
        {
            if (!_states.TryGetValue(key, out var st)) _states[key] = st = new RuleState(rule);
            var (holds, detail) = Check(rule);
            if (holds && st.TrueSince == 0) st.TrueSince = now;
            if (!holds) st.TrueSince = 0;
            var sustained = holds && (rule.When != "cpu-above" || now - st.TrueSince >= CpuSustainSec * 1000);

            if (sustained && !st.Active)
            {
                st.Active = true;
                log.LogInformation("rule {Id} fired ({Detail})", rule.Id, detail);
                Do(rule, detail);
            }
            else if (!holds && st.Active)
            {
                st.Active = false;
                log.LogInformation("rule {Id} ended", rule.Id);
                if (rule.WhileActive) Undo(rule);
            }
            if (st.Active) active.Add(rule.Id);
        }
        _active = [.. active];
    }

    /// <summary>Runs a rule's action once, regardless of its condition (the Test button in the settings).</summary>
    public void Test(RuleConfig rule) => Do(rule, "test");

    private void Do(RuleConfig r, string detail)
    {
        switch (r.Then)
        {
            case "goto" when !string.IsNullOrWhiteSpace(r.Page):
                push.Emit("goto", JsonSerializer.SerializeToElement(new { page = r.Page, rule = r.Id, hold = r.WhileActive }));
                break;
            case "notify":
                var title = r.Name.Length > 0 ? r.Name : "PhoneBox";
                var text = string.IsNullOrWhiteSpace(r.Text) ? detail : r.Text.Replace("{detail}", detail);
                push.Emit("notify", JsonSerializer.SerializeToElement(new { title, text, level = r.Level ?? "info" }));
                break;
            case "push" when !string.IsNullOrWhiteSpace(r.Key) && PushStore.ValidKey(r.Key):
                var value = (r.PushValue ?? "true").Replace("{detail}", detail);
                push.Set(r.Key, PushStore.ParseValue(value), null);
                break;
        }
    }

    private void Undo(RuleConfig r)
    {
        if (r.Then == "goto") push.Emit("back", JsonSerializer.SerializeToElement(new { rule = r.Id }));
        else if (r.Then == "push" && !string.IsNullOrWhiteSpace(r.Key)) push.Remove(r.Key);
    }

    private (bool Holds, string Detail) Check(RuleConfig r)
    {
        var match = r.Match?.Trim() ?? "";
        var names = match.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (r.When)
        {
            case "session-waiting":
            case "session-busy":
            {
                var status = r.When == "session-waiting" ? "waiting" : "busy";
                var s = sessions.Snapshot().FirstOrDefault(s => s.Status == status && (names.Length == 0
                    || names.Any(n => s.Project.Contains(n, StringComparison.OrdinalIgnoreCase) || s.Tool.Equals(n, StringComparison.OrdinalIgnoreCase))));
                return s is null ? (false, "") : (true, status == "waiting" ? $"{s.Project}: {s.WaitingFor ?? "needs you"}" : $"{s.Project} is working");
            }
            case "media-playing":
            {
                var m = media.Snapshot();
                var ok = m is { Playing: true } && (names.Length == 0 || names.Any(n => m.App.Contains(n, StringComparison.OrdinalIgnoreCase)));
                return ok ? (true, string.IsNullOrEmpty(m!.Artist) ? m.Title : $"{m.Title} · {m.Artist}") : (false, "");
            }
            case "process-running":
            {
                var procs = Processes();
                var hit = names.Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
                    .FirstOrDefault(n => procs.Contains(n) || procs.Any(p => p.Length >= 15 && n.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
                return hit is null ? (false, "") : (true, hit);
            }
            case "mic-in-use":
            case "camera-in-use":
            {
                var p = privacy.Snapshot();
                var apps = (r.When == "mic-in-use" ? p?.MicApps : p?.CameraApps) ?? [];
                var hit = apps.FirstOrDefault(a => names.Length == 0 || names.Any(n => a.Contains(n, StringComparison.OrdinalIgnoreCase)));
                return hit is null ? (false, "") : (true, hit);
            }
            case "cpu-above":
            {
                var cpu = system.Snapshot()?.Cpu;
                return cpu >= (r.Value ?? 85) ? (true, $"CPU {cpu:0}%") : (false, "");
            }
            case "idle":
            {
                var minutes = r.Value ?? 5;
                return IdleSeconds() >= minutes * 60 ? (true, $"away for {minutes:0} min") : (false, "");
            }
            case "time":
                return InWindow(match, DateTime.Now.TimeOfDay) ? (true, match) : (false, "");
            case "push":
            {
                if (match.Length == 0 || !push.Snapshot().TryGetValue(match, out var item)) return (false, "");
                var d = item.Data;
                var truthy = d.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.Number => d.GetDouble() != 0,
                    JsonValueKind.String => d.GetString() is { Length: > 0 } t && t is not ("0" or "false" or "off" or "no"),
                    JsonValueKind.Object or JsonValueKind.Array => true,
                    _ => false,
                };
                return truthy ? (true, d.ValueKind == JsonValueKind.String ? d.GetString()! : d.ToString()) : (false, "");
            }
        }
        return (false, "");
    }

    /// <summary>"22:00-07:00" (may wrap past midnight) or a single "08:30" (that minute).</summary>
    public static bool InWindow(string window, TimeSpan now)
    {
        var parts = window.Split('-', StringSplitOptions.TrimEntries);
        if (!TimeSpan.TryParse(parts[0], out var from)) return false;
        var to = parts.Length > 1 && TimeSpan.TryParse(parts[1], out var t) ? t : from + TimeSpan.FromMinutes(1);
        return from <= to ? now >= from && now < to : now >= from || now < to;
    }

    private HashSet<string> Processes()
    {
        if (_procs is not null && Environment.TickCount64 - _procsAt < 3000) return _procs;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
            using (p) { try { set.Add(p.ProcessName); } catch { } }
        _procsAt = Environment.TickCount64;
        return _procs = set;
    }

    private double IdleSeconds()
    {
        if (Environment.TickCount64 - _idleAt < 5000) return _idle;
        _idleAt = Environment.TickCount64;
        return _idle = Idle.Seconds();
    }
}
