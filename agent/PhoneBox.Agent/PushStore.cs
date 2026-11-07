using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record PushItem(JsonElement Data, long At, long? ExpiresAt);

/// <param name="Kind">notify | goto | back | refresh | reload | approval | approval-done</param>
public sealed record PanelEvent(long Seq, string Kind, JsonElement? Data, long At);

/// <summary>
/// Data pushed into the agent from outside (scripts, CI, Home Assistant, cron jobs…) through the webhook API,
/// plus a short queue of events for the phone (notifications, page switches, forced refreshes).
/// Values survive restarts (data.json next to config.json); events are kept in memory only.
/// </summary>
public sealed partial class PushStore
{
    public const int MaxBytes = 64 * 1024;
    private const int MaxItems = 200;
    private const int MaxEvents = 50;

    private static readonly string DataPath = Path.Combine(AppPaths.ConfigDir, "data.json");
    private readonly ConcurrentDictionary<string, PushItem> _items = new();
    private readonly Lock _evLock = new();
    private readonly LinkedList<PanelEvent> _events = new();
    private readonly ILogger<PushStore> _log;
    private readonly System.Threading.Timer _saveTimer;
    private long _seq;
    private long _version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public PushStore(ILogger<PushStore> log)
    {
        _log = log;
        _saveTimer = new System.Threading.Timer(_ => Persist(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (File.Exists(DataPath))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, PushItem>>(File.ReadAllText(DataPath)) ?? [])
                    if (ValidKey(k)) _items[k] = v;
        }
        catch (Exception ex) { log.LogWarning("could not read {Path}: {Msg}", DataPath, ex.Message); }
    }

    /// <summary>Changes whenever a value is set or removed; the phone refetches /api/push when it changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Last event number, so a freshly started phone does not replay old notifications.</summary>
    public long Seq => Interlocked.Read(ref _seq);

    public static bool ValidKey(string key) => KeyRegex().IsMatch(key);

    public IReadOnlyDictionary<string, PushItem> Snapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _items.Where(kv => kv.Value.ExpiresAt is null || kv.Value.ExpiresAt > now).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public PushItem? Get(string key) => Snapshot().GetValueOrDefault(key);

    /// <returns>An error message, or null on success.</returns>
    public string? Set(string key, JsonElement data, int? ttlSec)
    {
        if (!ValidKey(key)) return "key must be 1-64 characters: letters, digits, '.', '_' or '-'";
        if (!_items.ContainsKey(key) && _items.Count >= MaxItems) return $"too many keys (max {MaxItems}); delete some first";
        // {"ttl": 300, ...} inside the payload works too
        if (ttlSec is null && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("ttl", out var t) && t.TryGetInt32(out var tt)) ttlSec = tt;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _items[key] = new PushItem(data.Clone(), now, ttlSec is > 0 ? now + ttlSec.Value * 1000L : null);
        Changed();
        return null;
    }

    public bool Remove(string key)
    {
        var ok = _items.TryRemove(key, out _);
        if (ok) Changed();
        return ok;
    }

    public void Clear()
    {
        _items.Clear();
        Changed();
    }

    /// <summary>Raised after an event is queued, so live streams can deliver it right away.</summary>
    public event Action? Emitted;

    public PanelEvent Emit(string kind, JsonElement? data = null)
    {
        PanelEvent e;
        lock (_evLock)
        {
            e = new PanelEvent(++_seq, kind, data?.Clone(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _events.AddLast(e);
            while (_events.Count > MaxEvents) _events.RemoveFirst();
        }
        Emitted?.Invoke();
        return e;
    }

    /// <summary>Events after <paramref name="seq"/>, at most a minute old (a phone that was away does not get a flood).</summary>
    public IReadOnlyList<PanelEvent> Since(long seq)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        lock (_evLock) return _events.Where(e => e.Seq > seq && e.At > cutoff).ToList();
    }

    private void Changed()
    {
        Interlocked.Exchange(ref _version, Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Version + 1));
        _saveTimer.Change(2000, Timeout.Infinite); // debounce disk writes
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            var tmp = DataPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Snapshot()));
            File.Move(tmp, DataPath, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning("could not save pushed data: {Msg}", ex.Message); }
    }

    /// <summary>Reads a webhook body: JSON as-is, anything else as a trimmed string; ?value= wins when given.</summary>
    public static async Task<(JsonElement? Data, string? Error)> ReadBody(HttpRequest req)
    {
        if (req.Query.TryGetValue("value", out var qv)) return (Parse(qv.ToString()), null);
        if (req.ContentLength > MaxBytes) return (null, $"body too large (max {MaxBytes / 1024} KB)");
        var buf = new char[MaxBytes + 1];
        using var r = new StreamReader(req.Body);
        var n = await r.ReadBlockAsync(buf, 0, buf.Length);
        if (n > MaxBytes) return (null, $"body too large (max {MaxBytes / 1024} KB)");
        var text = new string(buf, 0, n).Trim();
        if (req.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try { return (JsonDocument.Parse(text == "" ? "null" : text).RootElement.Clone(), null); }
            catch (JsonException ex) { return (null, "invalid JSON: " + ex.Message); }
        }
        return (Parse(text), null);
    }

    /// <summary>"21.5" becomes a number, "true" a boolean, anything else a string.</summary>
    public static JsonElement ParseValue(string text) => Parse(text);

    private static JsonElement Parse(string text)
    {
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            return JsonSerializer.SerializeToElement(d);
        if (text is "true" or "false") return JsonSerializer.SerializeToElement(text == "true");
        return JsonSerializer.SerializeToElement(text);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$")]
    private static partial Regex KeyRegex();
}

/// <summary>Lets the webhook API wake up slow monitors (quotas, GitHub) immediately.</summary>
public static class Wake
{
    private static TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static long _count;

    /// <summary>Goes up on every <see cref="Now"/>, so a loop can tell a wake-up from its own timer.</summary>
    public static long Count => Interlocked.Read(ref _count);

    public static void Now()
    {
        Interlocked.Increment(ref _count);
        Interlocked.Exchange(ref _tcs, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>Like Task.Delay, but returns early when <see cref="Now"/> is called.</summary>
    public static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        await Task.WhenAny(Task.Delay(delay, ct), _tcs.Task);
        ct.ThrowIfCancellationRequested();
    }
}
