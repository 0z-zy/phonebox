using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record UpdateStatus(string AgentVersion, string? AppVersion, string? Latest, string? ReleaseUrl,
    bool AgentUpdate, bool AppUpdate, long? LastCheck, string State, string? Error);

/// <summary>
/// Keeps the agent and the phone app up to date from GitHub releases (PhoneBox-&lt;v&gt;.apk and
/// PhoneBoxAgent-&lt;v&gt;-&lt;rid&gt;.zip, rid = win-x64, linux-x64, linux-arm64, osx-arm64 or osx-x64):
///  - phone: installed with "adb install -r" while the phone is on USB (no prompt on the phone). Android refuses
///    an APK signed with a different key, and the agent never uninstalls to force it.
///  - agent: files are swapped in place (running files are renamed to *.old, which Windows allows) and the agent
///    restarts itself. Only when running from the installed location, never from a build folder.
/// </summary>
public sealed class UpdateService(ConfigStore cfg, AdbReverse adb, IHostApplicationLifetime life, ILogger<UpdateService> log) : BackgroundService
{
    private static readonly string InstallDir = AppPaths.InstallDir;
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "PhoneBoxAgent.exe" : "PhoneBoxAgent";
    private static readonly string Downloads = Path.Combine(AppPaths.LocalDir, "updates");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Release? _latest;
    private long? _lastCheck;
    private string _state = "idle";
    private string? _error;

    /// <summary>Phone app version, reported by the app on every request (X-PhoneBox-App header).</summary>
    public static volatile string? AppVersion;

    public static bool RunningInstalled =>
        string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/'), Path.GetFullPath(InstallDir).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public UpdateStatus Status()
    {
        var latest = _latest?.Version;
        return new UpdateStatus(AppPaths.Version, AppVersion, latest, _latest?.Url,
            latest is not null && Newer(latest, AppPaths.Version) && _latest!.AgentZip is not null,
            latest is not null && AppVersion is not null && Newer(latest, AppVersion) && _latest!.Apk is not null,
            _lastCheck, _state, _error);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        CleanupOldFiles();
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        var nextCheck = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= nextCheck)
            {
                await CheckAsync(ct);
                nextCheck = DateTime.UtcNow.AddHours(6);
            }
            if (cfg.Get().Updates.Auto) await ApplyAsync(ct);
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _state = "checking";
            var repo = cfg.Get().Updates.Repo;
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct));
            var r = doc.RootElement;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            string? apk = null, zip = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (name.StartsWith("PhoneBox-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) apk = url;
                if (name.StartsWith("PhoneBoxAgent-", StringComparison.OrdinalIgnoreCase) && name.EndsWith($"-{AppPaths.Rid}.zip", StringComparison.OrdinalIgnoreCase)) zip = url;
            }
            _latest = new Release(tag.TrimStart('v', 'V'), r.GetProperty("html_url").GetString(), apk, zip);
            _lastCheck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _error = null;
            _state = "idle";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _error = "Update check failed: " + ex.Message;
            _state = "idle";
        }
        finally { _gate.Release(); }
    }

    /// <summary>Installs whatever is newer: the phone app first (needs USB), then the agent (restarts it).</summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        var s = Status();
        if (!s.AppUpdate && !s.AgentUpdate) return;
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            if (s.AppUpdate && adb.Connected) await UpdatePhoneAsync(ct);
            if (s.AgentUpdate && RunningInstalled) await UpdateAgentAsync(ct);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            log.LogWarning("update failed: {Msg}", ex.Message);
        }
        finally
        {
            if (_state != "restarting") _state = "idle";
            _gate.Release();
        }
    }

    private async Task UpdatePhoneAsync(CancellationToken ct)
    {
        _state = "updating phone app";
        var apk = await DownloadAsync(_latest!.Apk!, $"PhoneBox-{_latest.Version}.apk", ct);
        var adbExe = AdbReverse.FindAdb(cfg.Get().AdbPath) ?? throw new InvalidOperationException("adb not found");
        foreach (var serial in adb.Devices.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var output = await Run(adbExe, $"-s {serial} install -r \"{apk}\"", ct);
            if (!output.Contains("Success"))
                throw new InvalidOperationException($"phone update on {serial} failed: {output.Trim().Split('\n').LastOrDefault()}");
            await Run(adbExe, $"-s {serial} shell am start -n {AdbReverse.PanelActivity}", ct);
            log.LogInformation("phone app on {Serial} updated to {Version}", serial, _latest.Version);
        }
        AppVersion = _latest.Version;
    }

    private async Task UpdateAgentAsync(CancellationToken ct)
    {
        _state = "updating agent";
        var zip = await DownloadAsync(_latest!.AgentZip!, $"PhoneBoxAgent-{_latest.Version}.zip", ct);
        var staging = Path.Combine(Downloads, "agent-" + _latest.Version);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        if (!File.Exists(Path.Combine(staging, ExeName))) throw new InvalidOperationException($"the agent package has no {ExeName}");

        foreach (var src in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(staging, src);
            var dst = Path.Combine(InstallDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (File.Exists(dst))
            {
                var old = dst + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(dst, old); // works even while the file is in use
            }
            File.Move(src, dst);
        }
        if (!OperatingSystem.IsWindows()) // zip files do not keep the executable bit
            File.SetUnixFileMode(Path.Combine(InstallDir, ExeName), (UnixFileMode)0b111_101_101);
        log.LogInformation("agent updated to {Version}, restarting", _latest.Version);
        _state = "restarting";
        AppRestart.Requested = true;
        life.StopApplication();
    }

    private async Task<string> DownloadAsync(string url, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(Downloads);
        var path = Path.Combine(Downloads, name);
        if (File.Exists(path)) return path;
        var tmp = path + ".part";
        await using (var src = await _http.GetStreamAsync(url, ct))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst, ct);
        File.Move(tmp, path, true);
        return path;
    }

    /// <summary>Removes *.old files left behind by the previous update, plus old downloads.</summary>
    private static void CleanupOldFiles()
    {
        try
        {
            if (Directory.Exists(InstallDir))
                foreach (var f in Directory.EnumerateFiles(InstallDir, "*.old", SearchOption.AllDirectories))
                    try { File.Delete(f); } catch { }
            if (Directory.Exists(Downloads))
                foreach (var f in Directory.EnumerateFileSystemEntries(Downloads).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)))
                    try { if (Directory.Exists(f)) Directory.Delete(f, true); else File.Delete(f); } catch { }
        }
        catch { }
    }

    private static bool Newer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current.Split('-')[0], out var b) && a > b;

    private static async Task<string> Run(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppPaths.NeutralCwd,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return await stdout + await stderr;
    }

    private sealed record Release(string Version, string? Url, string? Apk, string? AgentZip);
}

/// <summary>Set before stopping the host to make Program start a fresh instance afterwards.</summary>
public static class AppRestart
{
    public static volatile bool Requested;
}
