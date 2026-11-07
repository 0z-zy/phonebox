using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed class AgentConfig
{
    public string? DeviceSerial { get; set; }
    public string? AdbPath { get; set; }
    public WeatherConfig Weather { get; set; } = new();
    public NightConfig Night { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();
    public LanConfig Lan { get; set; } = new();
    public UpdateConfig Updates { get; set; } = new();
    public List<PageConfig> Pages { get; set; } = [];
    public List<Shortcut> Shortcuts { get; set; } = [];

    /// <summary>The part sent to the phone: commands (cmd/args/cwd) are never included.</summary>
    public object ToPublic() => new
    {
        weather = Weather,
        night = Night,
        display = Display,
        pages = Pages,
        shortcuts = Shortcuts.Select(s => new { s.Id, s.Label, s.Icon, s.Color }),
    };

    public bool PageEnabled(string type) => Pages.FirstOrDefault(p => p.Type == type)?.Enabled ?? false;

    public bool PageOption(string type, string option, bool fallback) =>
        Pages.FirstOrDefault(p => p.Type == type)?.Options is { } o && o.TryGetValue(option, out var v)
            ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback)
            : fallback;
}

public sealed class WeatherConfig
{
    public string City { get; set; } = "Tallinn";
    public double Lat { get; set; } = 59.437;
    public double Lon { get; set; } = 24.745;
}

public sealed class NightConfig
{
    public bool Enabled { get; set; } = true;
    public string Start { get; set; } = "00:30";
    public string End { get; set; } = "07:30";
    public double Brightness { get; set; } = 0.02;
}

public sealed class DisplayConfig
{
    public bool ShowSeconds { get; set; } = true;
    /// <summary>Return to the first page after this many idle seconds; 0 = never.</summary>
    public int IdleReturnSec { get; set; } = 90;
    /// <summary>Vibrate when a session finishes or needs input.</summary>
    public bool Vibrate { get; set; } = true;
}

public sealed class LanConfig
{
    /// <summary>Also accept the phone over Wi-Fi (HTTPS + token, paired over USB). Requires an agent restart.</summary>
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8766;
    /// <summary>Shared secret handed to the phone over USB. Empty = generate a new one (forces re-pairing).</summary>
    public string? Token { get; set; }
}

public sealed class UpdateConfig
{
    /// <summary>Install new GitHub releases automatically (phone app over USB, agent in place).</summary>
    public bool Auto { get; set; } = true;
    public string Repo { get; set; } = "ersinkoc/phonebox";
}

public sealed class PageConfig
{
    /// <summary>home | ai | work | system | procs | deck</summary>
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, JsonElement>? Options { get; set; }
}

public sealed class Shortcut
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "#6b7280";
    public string Cmd { get; set; } = "";
    public string? Args { get; set; }
    public string? Cwd { get; set; }
}

/// <summary>Reads/writes config.json; external edits are picked up on the next request.</summary>
public sealed partial class ConfigStore(string path)
{
    public static readonly string[] PageTypes = ["home", "ai", "work", "system", "procs", "deck"];

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep emoji and non-ASCII text readable
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _lock = new();
    private AgentConfig _config = Normalize(new AgentConfig());
    private DateTime _stamp = DateTime.MinValue;

    public string Path => path;

    /// <summary>Version stamp (file write time) so the phone can notice changes.</summary>
    public long Version { get { Get(); return _stamp.Ticks; } }

    public AgentConfig Get()
    {
        lock (_lock)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp != _stamp)
                {
                    _config = Normalize(JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), ReadOpts) ?? new());
                    _stamp = stamp;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"could not read config ({path}): {ex.Message}");
            }
            return _config;
        }
    }

    /// <summary>Validates and saves the settings. Returns an error message, or null on success.</summary>
    public string? Save(string json)
    {
        AgentConfig cfg;
        try { cfg = JsonSerializer.Deserialize<AgentConfig>(json, ReadOpts) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { return "Invalid JSON: " + ex.Message; }

        cfg = Normalize(cfg);
        foreach (var s in cfg.Shortcuts)
            if (string.IsNullOrWhiteSpace(s.Cmd)) return $"Shortcut '{s.Label}' has no command";
        if (cfg.Weather.Lat is < -90 or > 90 || cfg.Weather.Lon is < -180 or > 180) return "Invalid weather location";
        if (!HhMm().IsMatch(cfg.Night.Start) || !HhMm().IsMatch(cfg.Night.End)) return "Night mode times must be in HH:MM format";

        lock (_lock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, WriteOpts));
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.Move(tmp, path, overwrite: true);
            _config = cfg;
            _stamp = File.GetLastWriteTimeUtc(path);
        }
        return null;
    }

    /// <summary>Fills in defaults: drops unknown pages, appends missing pages, generates unique shortcut ids.</summary>
    private static AgentConfig Normalize(AgentConfig c)
    {
        c.Weather ??= new();
        c.Night ??= new();
        c.Display ??= new();
        c.Display.IdleReturnSec = Math.Clamp(c.Display.IdleReturnSec, 0, 3600);
        c.Lan ??= new();
        c.Updates ??= new();
        if (string.IsNullOrWhiteSpace(c.Updates.Repo)) c.Updates.Repo = "ersinkoc/phonebox";
        c.Lan.Port = Math.Clamp(c.Lan.Port, 1024, 65535);
        if (string.IsNullOrWhiteSpace(c.Lan.Token)) c.Lan.Token = NewToken();
        c.Night.Brightness = Math.Clamp(c.Night.Brightness, 0.01, 1);

        var pages = (c.Pages ?? []).Where(p => PageTypes.Contains(p.Type)).DistinctBy(p => p.Type).ToList();
        foreach (var t in PageTypes)
            if (pages.All(p => p.Type != t)) pages.Add(new PageConfig { Type = t, Enabled = true });
        c.Pages = pages;

        var used = new HashSet<string>();
        foreach (var s in c.Shortcuts ??= [])
        {
            s.Label = s.Label?.Trim() ?? "";
            var id = string.IsNullOrWhiteSpace(s.Id) ? Slug(s.Label) : Slug(s.Id);
            if (id == "") id = "shortcut";
            var unique = id;
            for (var i = 2; !used.Add(unique); i++) unique = $"{id}-{i}";
            s.Id = unique;
            if (string.IsNullOrWhiteSpace(s.Args)) s.Args = null;
            if (string.IsNullOrWhiteSpace(s.Cwd)) s.Cwd = null;
        }
        return c;
    }

    private static string NewToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Makes sure the generated Wi-Fi token is written to disk so it stays stable across restarts.</summary>
    public void PersistGenerated()
    {
        var c = Get();
        try
        {
            var onDisk = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), ReadOpts);
            if (onDisk?.Lan?.Token == c.Lan.Token) return;
        }
        catch { }
        Save(JsonSerializer.Serialize(c, WriteOpts));
    }

    private static string Slug(string s) => NonSlug().Replace(s.ToLowerInvariant()
        .Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u').Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c'), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex HhMm();
}
