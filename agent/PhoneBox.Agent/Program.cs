using System.Text.Json;
using PhoneBox.Agent;

const int Port = 8765;
string[] SameOrigins = [$"http://127.0.0.1:{Port}", $"http://localhost:{Port}"];

// Single instance: a second launch opens the running instance's settings page and exits.
using var single = new Mutex(true, @"Local\PhoneBox.Agent", out var firstInstance);
if (!firstInstance)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://127.0.0.1:{Port}/") { UseShellExecute = true }); } catch { }
    return;
}

AppPaths.EnsureConfig();
var configPath = args.SkipWhile(a => a != "--config").Skip(1).FirstOrDefault() ?? AppPaths.ConfigPath;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddProvider(new FileLoggerProvider());
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

builder.Services.AddSingleton(new ConfigStore(configPath));
builder.Services.AddSingleton<AudioControl>();
AddMonitor<SystemMonitor>(builder.Services);
AddMonitor<MediaMonitor>(builder.Services);
AddMonitor<QuotaMonitor>(builder.Services);
AddMonitor<ProcessMonitor>(builder.Services);
AddMonitor<SessionMonitor>(builder.Services);
AddMonitor<GitHubMonitor>(builder.Services);
AddMonitor<PingMonitor>(builder.Services);
AddMonitor<AdbReverse>(builder.Services);
builder.Services.AddHostedService<Tray>();

var app = builder.Build();

// Security:
//  - Listens on 127.0.0.1 only (not reachable from the network); the Host check blocks DNS rebinding.
//  - The phone's local proxy sends no Origin header. From browsers only the settings page itself (same origin)
//    is accepted; requests from any other site (CSRF) are rejected.
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host;
    var origin = ctx.Request.Headers.Origin.ToString();
    if ((host != "127.0.0.1" && host != "localhost") || (origin != "" && !SameOrigins.Contains(origin)))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});

// ---------------------------------------------------------------- settings UI (browser)
app.MapGet("/", () =>
{
    using var s = AppPaths.Resource("settings.html")!;
    using var r = new StreamReader(s);
    return Results.Content(r.ReadToEnd(), "text/html; charset=utf-8");
});
app.MapGet("/favicon.ico", () => AppPaths.Resource("phonebox.ico") is { } s ? Results.Stream(s, "image/x-icon") : Results.NotFound());

app.MapGet("/api/settings", (ConfigStore cfg) => Results.Text(
    JsonSerializer.Serialize(cfg.Get(), ConfigStore.WriteOpts), "application/json; charset=utf-8"));

app.MapPut("/api/settings", async (HttpRequest req, ConfigStore cfg, ILogger<Program> log) =>
{
    if (req.ContentType?.StartsWith("application/json") != true) return Results.StatusCode(415);
    using var r = new StreamReader(req.Body);
    var err = cfg.Save(await r.ReadToEndAsync());
    if (err is not null) return Results.BadRequest(new { error = err });
    log.LogInformation("settings saved");
    return Results.Ok(new { version = cfg.Version });
});

app.MapPost("/api/shortcuts/test", async (HttpRequest req, ILogger<Program> log) =>
{
    if (req.ContentType?.StartsWith("application/json") != true) return Results.StatusCode(415);
    var sc = await req.ReadFromJsonAsync<PhoneBox.Agent.Shortcut>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (sc is null || string.IsNullOrWhiteSpace(sc.Cmd)) return Results.BadRequest(new { error = "command is empty" });
    var err = Shortcuts.Execute(sc, log);
    return err is null ? Results.Ok() : Results.BadRequest(new { error = err });
});

app.MapGet("/api/info", (AdbReverse adb, ConfigStore cfg) => Results.Json(new
{
    version = AppPaths.Version,
    configPath = cfg.Path,
    logPath = AppPaths.LogPath,
    adb = AdbReverse.FindAdb(cfg.Get().AdbPath),
    connected = adb.Connected,
    devices = adb.AllDevices,
}));

app.MapPost("/api/phone/open", async (AdbReverse adb) => { await adb.OpenPanelAsync(); return Results.Ok(); });

// ---------------------------------------------------------------- phone panel API
app.MapGet("/api/state", (SystemMonitor sys, MediaMonitor media, AudioControl audio, PingMonitor ping, ConfigStore cfg) => Results.Json(new
{
    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    host = Environment.MachineName,
    cfgVersion = cfg.Version,
    pc = sys.Snapshot(),
    media = media.Snapshot(),
    audio = audio.Snapshot(),
    ping = ping.Snapshot(),
}));

app.MapGet("/api/config", (ConfigStore cfg) => Results.Json(new { version = cfg.Version, config = cfg.Get().ToPublic() }));

app.MapGet("/api/art", (MediaMonitor media) =>
    media.Art is { } art ? Results.File(art.Bytes, art.Mime) : Results.NotFound());

app.MapPost("/api/media/{action}", async (string action, long? ms, MediaMonitor media) =>
    await media.Control(action, ms) ? Results.Ok() : Results.BadRequest());

app.MapPost("/api/volume", (int? level, string? op, AudioControl audio) =>
{
    var ok = op switch
    {
        "mute" => audio.ToggleMute(),
        "up" => audio.Step(+5),
        "down" => audio.Step(-5),
        _ when level is { } l => audio.SetVolume(l),
        _ => false,
    };
    return ok ? Results.Json(audio.Snapshot()) : Results.BadRequest();
});

app.MapGet("/api/quotas", (QuotaMonitor q) => Results.Json(new { providers = q.Snapshot() }));

app.MapGet("/api/work", (SessionMonitor s, GitHubMonitor gh) => Results.Json(new
{
    sessions = s.Snapshot(),
    github = gh.Snapshot(),
}));

app.MapGet("/api/procs", (ProcessMonitor procs, int? top) =>
{
    var all = procs.Snapshot();
    return Results.Json(new { total = all.Sum(g => g.Count), groupCount = all.Count, groups = all.Take(Math.Clamp(top ?? 40, 1, 200)) });
});

app.MapGet("/api/procs/icon", (string name, ProcessMonitor procs) =>
    procs.Icon(name) is { } png ? Results.File(png, "image/png") : Results.NotFound());

app.MapGet("/api/procs/detail", (string name, ProcessMonitor procs) => Results.Json(procs.Detail(name)));

// ?name=X ends every instance of X; ?pid=N ends one process together with its children.
app.MapPost("/api/procs/kill", (string? name, int? pid, ProcessMonitor procs, ConfigStore cfg) =>
{
    if (!cfg.Get().PageOption("procs", "allowKill", true))
        return Results.Json(new KillResult(0, 0, "Terminating processes is disabled in the settings"), statusCode: 403);
    var r = pid is { } id ? procs.KillPid(id) : procs.Kill(name ?? "");
    return r.Killed > 0 ? Results.Json(r) : Results.Json(r, statusCode: 409);
});

app.MapPost("/api/run/{id}", (string id, ConfigStore cfg, ILogger<Program> log) =>
{
    var result = Shortcuts.Run(cfg.Get(), id, log);
    return result is null ? Results.Ok() : Results.Problem(result);
});

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("PhoneBox agent {Version} ready on http://127.0.0.1:{Port} (config: {Path})", AppPaths.Version, Port, configPath));

app.Run();

static void AddMonitor<T>(IServiceCollection s) where T : class, IHostedService
{
    s.AddSingleton<T>();
    s.AddHostedService(sp => sp.GetRequiredService<T>());
}
