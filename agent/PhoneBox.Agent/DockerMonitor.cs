using System.Diagnostics;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record DockerContainer(string Id, string Name, string Image, string State, string Status, string? Health,
    double? Cpu, string? Mem, string? Project);

public sealed record DockerSnapshot(bool Available, string? Error, IReadOnlyList<DockerContainer> Containers, long Updated);

/// <summary>
/// Docker (or Podman) containers for the Docker widget: <c>docker ps -a</c> every few seconds and <c>docker stats</c>
/// every 15 seconds, only while a phone is asking. Start / stop / restart are only allowed when a Docker widget in the
/// settings has "Allow start / stop" turned on.
/// </summary>
public sealed class DockerMonitor(ConfigStore config, ILogger<DockerMonitor> log) : BackgroundService
{
    private volatile DockerSnapshot _snap = new(false, null, [], 0);
    private long _lastRequest;
    private Dictionary<string, (double Cpu, string Mem)> _stats = [];
    private DateTime _statsAt = DateTime.MinValue;
    private static readonly Lazy<string?> Cli = new(FindCli);

    public DockerSnapshot Snapshot()
    {
        Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
        return _snap;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastRequest) < 30_000)
            {
                try { await Refresh(ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { _snap = _snap with { Error = ex.Message }; }
                await Task.Delay(4000, ct);
            }
            else await Task.Delay(500, ct);
        }
    }

    private async Task Refresh(CancellationToken ct)
    {
        if (Cli.Value is null) { _snap = new(false, "Docker is not installed (no docker or podman command found)", [], Now()); return; }
        var (code, output, error) = await Run(ct, "ps", "-a", "--format", "{{json .}}");
        if (code != 0) { _snap = new(true, FirstLine(error) ?? "docker ps failed", _snap.Containers, Now()); return; }

        if (DateTime.UtcNow - _statsAt > TimeSpan.FromSeconds(15))
        {
            _statsAt = DateTime.UtcNow;
            var (sc, so, _) = await Run(ct, "stats", "--no-stream", "--format", "{{json .}}");
            if (sc == 0)
            {
                var stats = new Dictionary<string, (double, string)>();
                foreach (var line in so.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var j = JsonDocument.Parse(line).RootElement;
                        var id = Str(j, "ID") ?? Str(j, "Container") ?? "";
                        var cpu = double.TryParse(Str(j, "CPUPerc")?.TrimEnd('%'), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0;
                        stats[id.Length > 12 ? id[..12] : id] = (cpu, Str(j, "MemUsage")?.Split('/')[0].Trim() ?? "");
                    }
                    catch { }
                }
                _stats = stats;
            }
        }

        var list = new List<DockerContainer>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var j = JsonDocument.Parse(line).RootElement;
                var id = Str(j, "ID") ?? "";
                var shortId = id.Length > 12 ? id[..12] : id;
                var labels = Str(j, "Labels") ?? "";
                var project = labels.Split(',').FirstOrDefault(l => l.StartsWith("com.docker.compose.project="))?.Split('=', 2)[1];
                var health = Str(j, "HealthStatus") is { } hs && hs != "none" && hs != "" ? hs : null;
                var stat = _stats.TryGetValue(shortId, out var s) ? s : ((double, string)?)null;
                list.Add(new DockerContainer(shortId, (Str(j, "Names") ?? shortId).TrimStart('/'), Str(j, "Image") ?? "",
                    (Str(j, "State") ?? "").ToLowerInvariant(), Str(j, "Status") ?? "", health, stat?.Item1, stat?.Item2, project));
            }
            catch { }
        }
        _snap = new(true, null, list.OrderBy(c => c.State == "running" ? 0 : 1).ThenBy(c => c.Name).ToList(), Now());
    }

    /// <summary>Whether some Docker widget allows start / stop / restart from the phone.</summary>
    public bool ControlAllowed() =>
        config.Get().Pages.SelectMany(p => p.Widgets ?? []).Any(w => w.Type == "docker" && w.Bool("control") == true);

    public async Task<string?> ActAsync(string id, string action)
    {
        if (!ControlAllowed()) return "starting and stopping containers is turned off in the widget's settings";
        if (action is not ("start" or "stop" or "restart")) return "unknown action";
        if (!_snap.Containers.Any(c => c.Id == id)) return "unknown container";
        var (code, _, error) = await Run(CancellationToken.None, action, id);
        log.LogInformation("docker {Action} {Id}: {Result}", action, id, code == 0 ? "ok" : error);
        _statsAt = DateTime.MinValue;
        Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
        try { await Refresh(CancellationToken.None); } catch { }
        return code == 0 ? null : FirstLine(error) ?? $"docker {action} failed";
    }

    private static async Task<(int Code, string Out, string Err)> Run(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(Cli.Value!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppPaths.NeutralCwd,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await stdout, await stderr);
    }

    private static string? FindCli()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsWindows()) dirs.Add(@"C:\Program Files\Docker\Docker\resources\bin");
        else dirs.AddRange(["/usr/local/bin", "/opt/homebrew/bin", "/usr/bin", "/Applications/Docker.app/Contents/Resources/bin"]);
        foreach (var name in new[] { "docker", "podman" })
            foreach (var d in dirs)
            {
                var f = Path.Combine(d.Trim(), name + exe);
                if (File.Exists(f)) return f;
            }
        return null;
    }

    private static string? Str(JsonElement j, string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
