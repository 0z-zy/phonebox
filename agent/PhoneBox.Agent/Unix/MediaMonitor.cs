using System.Globalization;

namespace PhoneBox.Agent;

/// <summary>
/// Now playing on Linux (MPRIS through playerctl: Spotify, browsers, VLC, Rhythmbox…) and macOS (Spotify and Music
/// through AppleScript). Same API as the Windows MediaMonitor.
/// </summary>
public sealed class MediaMonitor(ILogger<MediaMonitor> log) : BackgroundService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private volatile MediaSnapshot? _snap;
    private string _artUrl = "";
    private int _artId;
    private bool _warned;

    public AlbumArt? Art { get; private set; }
    public MediaSnapshot? Snapshot() => _snap;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Poll(); }
            catch (Exception ex) { log.LogDebug("media: {Msg}", ex.Message); _snap = null; }
            await Task.Delay(1000, ct);
        }
    }

    private async Task Poll()
    {
        var (snap, art) = OperatingSystem.IsMacOS() ? ReadMac() : ReadLinux();
        if (snap is null) { _snap = null; return; }
        if (art != _artUrl)
        {
            _artUrl = art;
            Art = await LoadArt(art);
            if (Art is not null) _artId++;
        }
        _snap = snap with { ArtId = Art is null ? 0 : _artId };
    }

    // ---------------------------------------------------------------- Linux: playerctl (MPRIS)
    private (MediaSnapshot?, string) ReadLinux()
    {
        if (Sh.Which("playerctl") is null)
        {
            if (!_warned) { _warned = true; log.LogInformation("media: install playerctl to show and control what is playing (sudo apt install playerctl)"); }
            return (null, "");
        }
        var line = Sh.Run("playerctl", "metadata", "--format",
            "{{playerName}}\t{{status}}\t{{title}}\t{{artist}}\t{{album}}\t{{position}}\t{{mpris:length}}\t{{mpris:artUrl}}");
        if (string.IsNullOrWhiteSpace(line)) return (null, "");
        var f = line.TrimEnd('\n').Split('\t');
        if (f.Length < 8) return (null, "");
        long Us(string s) => long.TryParse(s, out var v) ? v / 1000 : 0;
        var app = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(f[0].Split('.')[0]);
        return (new MediaSnapshot(app, f[2], f[3], f[4], f[1] == "Playing", Us(f[5]), Us(f[6]), 0, Us(f[6]) > 0), f[7]);
    }

    // ---------------------------------------------------------------- macOS: AppleScript (Spotify, Music)
    private static (MediaSnapshot?, string) ReadMac()
    {
        const string script = """
            if application "Spotify" is running then
              tell application "Spotify"
                if player state is stopped then return ""
                set t to current track
                return "Spotify" & tab & (player state as string) & tab & (name of t) & tab & (artist of t) & tab & (album of t) & tab & (player position as string) & tab & ((duration of t) / 1000 as string) & tab & (artwork url of t)
              end tell
            else if application "Music" is running then
              tell application "Music"
                if player state is stopped then return ""
                set t to current track
                return "Music" & tab & (player state as string) & tab & (name of t) & tab & (artist of t) & tab & (album of t) & tab & (player position as string) & tab & ((duration of t) as string) & tab & ""
              end tell
            end if
            return ""
            """;
        var line = Sh.Run("osascript", "-e", script)?.Trim();
        if (string.IsNullOrEmpty(line)) return (null, "");
        var f = line.Split('\t');
        if (f.Length < 8) return (null, "");
        long Sec(string s) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)(v * 1000) : 0;
        return (new MediaSnapshot(f[0], f[2], f[3], f[4], f[1] == "playing", Sec(f[5]), Sec(f[6]), 0, true), f[7]);
    }

    private async Task<AlbumArt?> LoadArt(string url)
    {
        try
        {
            if (url.StartsWith("file://"))
            {
                var path = Uri.UnescapeDataString(new Uri(url).AbsolutePath);
                return File.Exists(path) ? new AlbumArt(await File.ReadAllBytesAsync(path), path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg") : null;
            }
            if (url.StartsWith("https://") || url.StartsWith("http://"))
            {
                using var r = await Http.GetAsync(url);
                return r.IsSuccessStatusCode ? new AlbumArt(await r.Content.ReadAsByteArrayAsync(), r.Content.Headers.ContentType?.MediaType ?? "image/jpeg") : null;
            }
        }
        catch (Exception ex) { log.LogDebug("cover art: {Msg}", ex.Message); }
        return null;
    }

    public async Task<bool> Control(string action, long? ms)
    {
        bool ok;
        if (OperatingSystem.IsMacOS())
        {
            var app = _snap?.App ?? "Spotify";
            var cmd = action switch
            {
                "playpause" => "playpause",
                "next" => "next track",
                "prev" => "previous track",
                "seek" when ms is { } m => $"set player position to {(m / 1000.0).ToString(CultureInfo.InvariantCulture)}",
                _ => null,
            };
            ok = cmd is not null && Sh.Run("osascript", "-e", $"tell application \"{app}\" to {cmd}") is not null;
        }
        else
        {
            string[]? args = action switch
            {
                "playpause" => ["play-pause"],
                "next" => ["next"],
                "prev" => ["previous"],
                "seek" when ms is { } m => ["position", (m / 1000.0).ToString(CultureInfo.InvariantCulture)],
                _ => null,
            };
            ok = args is not null && Sh.Run("playerctl", args) is not null;
        }
        try { await Poll(); } catch { }
        return ok;
    }
}
