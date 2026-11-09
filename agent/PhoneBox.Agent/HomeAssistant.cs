using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record HaEntity(string Id, string State, string Name, string? Unit, string? DeviceClass, string? Icon, long Changed);

/// <summary>
/// Home Assistant over its REST API with a long-lived access token (Settings › General). The phone only sees the
/// entities listed in its Home Assistant widgets, and may only switch those whose widget allows it. The token never
/// leaves this computer.
/// </summary>
public sealed class HomeAssistant(ConfigStore config, ILogger<HomeAssistant> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private Dictionary<string, HaEntity> _states = [];
    private DateTime _statesAt = DateTime.MinValue;
    private string? _error;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IEnumerable<WidgetConfig> Widgets => config.Get().Widgets().Where(w => w.Type == "ha");

    public static IEnumerable<string> EntitiesOf(WidgetConfig w) =>
        (w.Str("entities") ?? "").Split([',', '\n', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.Contains('.'));

    /// <summary>States of the requested entities (only those used by a widget), refreshed at most every 3 seconds.</summary>
    public async Task<object> StatesAsync(IEnumerable<string> ids)
    {
        var ha = config.Get().HomeAssistant;
        if (string.IsNullOrWhiteSpace(ha.Url) || string.IsNullOrWhiteSpace(ha.Token))
            return new { configured = false, error = "Set up Home Assistant in Settings › General", entities = Array.Empty<HaEntity>() };
        var allowed = Widgets.SelectMany(EntitiesOf).ToHashSet();
        await _lock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _statesAt > TimeSpan.FromSeconds(3))
            {
                _statesAt = DateTime.UtcNow;
                try
                {
                    using var req = Request(HttpMethod.Get, "/api/states");
                    using var res = await Http.SendAsync(req);
                    if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Home Assistant answered {(int)res.StatusCode} {res.ReasonPhrase}");
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                    var map = new Dictionary<string, HaEntity>();
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        var id = e.GetProperty("entity_id").GetString()!;
                        if (!allowed.Contains(id)) continue;
                        var attrs = e.TryGetProperty("attributes", out var a) ? a : default;
                        string? A(string n) => attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        var changed = e.TryGetProperty("last_changed", out var lc) && DateTimeOffset.TryParse(lc.GetString(), out var dt) ? dt.ToUnixTimeMilliseconds() : 0;
                        map[id] = new HaEntity(id, e.GetProperty("state").GetString() ?? "", A("friendly_name") ?? id, A("unit_of_measurement"),
                            A("device_class"), A("icon"), changed);
                    }
                    _states = map;
                    _error = null;
                }
                catch (Exception ex)
                {
                    _error = ex is TaskCanceledException ? "Home Assistant did not answer" : ex.Message;
                    log.LogDebug("home assistant: {Msg}", _error);
                }
            }
            var list = ids.Where(allowed.Contains).Select(id => _states.TryGetValue(id, out var s) ? s : new HaEntity(id, "unknown", id, null, null, null, 0)).ToList();
            return new { configured = true, error = _error, entities = list };
        }
        finally { _lock.Release(); }
    }

    /// <summary>Switches an entity (toggle, press, run…) if a widget lists it with "Allow switching" on.</summary>
    public async Task<string?> ToggleAsync(string id)
    {
        if (!Widgets.Any(w => w.Bool("control") == true && EntitiesOf(w).Contains(id))) return "switching this entity is not allowed in the widget's settings";
        var domain = id.Split('.')[0];
        var state = _states.TryGetValue(id, out var s) ? s.State : "";
        var (svcDomain, service) = domain switch
        {
            "light" or "switch" or "fan" or "input_boolean" or "automation" or "siren" or "humidifier" or "group" => ("homeassistant", "toggle"),
            "cover" => ("cover", "toggle"),
            "media_player" => ("media_player", "media_play_pause"),
            "scene" => ("scene", "turn_on"),
            "script" => ("script", "turn_on"),
            "button" => ("button", "press"),
            "input_button" => ("input_button", "press"),
            "lock" => ("lock", state == "locked" ? "unlock" : "lock"),
            "vacuum" => ("vacuum", state == "cleaning" ? "return_to_base" : "start"),
            _ => ("", ""),
        };
        if (service == "") return $"{domain} entities cannot be switched";
        try
        {
            using var req = Request(HttpMethod.Post, $"/api/services/{svcDomain}/{service}");
            req.Content = new StringContent(JsonSerializer.Serialize(new { entity_id = id }), Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req);
            log.LogInformation("home assistant: {Service} {Id} -> {Code}", $"{svcDomain}.{service}", id, (int)res.StatusCode);
            _statesAt = DateTime.MinValue; // show the new state on the next poll
            return res.IsSuccessStatusCode ? null : $"Home Assistant answered {(int)res.StatusCode}";
        }
        catch (Exception ex) { return ex is TaskCanceledException ? "Home Assistant did not answer" : ex.Message; }
    }

    /// <summary>Turns an entity on or off (the charge guard's smart plug; set in the local-only settings).</summary>
    public async Task<string?> SwitchAsync(string id, bool on)
    {
        if (!id.Contains('.')) return "not an entity id";
        try
        {
            using var req = Request(HttpMethod.Post, $"/api/services/homeassistant/{(on ? "turn_on" : "turn_off")}");
            req.Content = new StringContent(JsonSerializer.Serialize(new { entity_id = id }), Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req);
            _statesAt = DateTime.MinValue;
            return res.IsSuccessStatusCode ? null : $"Home Assistant answered {(int)res.StatusCode}";
        }
        catch (Exception ex) { return ex is TaskCanceledException ? "Home Assistant did not answer" : ex.Message; }
    }

    /// <summary>Settings › Test: checks a URL and token (possibly unsaved).</summary>
    public static async Task<string> TestAsync(string url, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/api/");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await Http.SendAsync(req);
            return res.IsSuccessStatusCode ? "Connected" : res.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "The token was rejected" : $"Home Assistant answered {(int)res.StatusCode}";
        }
        catch (Exception ex) { return ex is TaskCanceledException ? "No answer (check the address)" : ex.Message; }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var ha = config.Get().HomeAssistant;
        var req = new HttpRequestMessage(method, ha.Url!.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ha.Token);
        return req;
    }
}
