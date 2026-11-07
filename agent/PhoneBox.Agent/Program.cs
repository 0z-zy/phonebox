using System.Net;
using System.Text.Json;
using PhoneBox.Agent;

const int Port = 8765;
string[] SameOrigins = [$"http://127.0.0.1:{Port}", $"http://localhost:{Port}"];

// Single instance: a second launch opens the running instance's settings page and exits.
// After a restart the previous instance may still be shutting down, so wait for it briefly.
var single = new Mutex(false, @"Local\PhoneBox.Agent");
var waitMs = args.Contains("--restart") ? 15_000 : 0;
bool firstInstance;
try { firstInstance = single.WaitOne(waitMs); }
catch (AbandonedMutexException) { firstInstance = true; }
if (!firstInstance)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://127.0.0.1:{Port}/") { UseShellExecute = true }); } catch { }
    return;
}

AppPaths.EnsureConfig();
var configPath = args.SkipWhile(a => a != "--config").Skip(1).FirstOrDefault() ?? AppPaths.ConfigPath;
var configStore = new ConfigStore(configPath);
configStore.PersistGenerated(); // keep the Wi-Fi token stable across restarts
var lanCfg = configStore.Get().Lan;
var lanCert = lanCfg.Enabled ? Lan.Certificate() : null;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Loopback, Port);
    // Wi-Fi / Tailscale: HTTPS with a pinned self-signed certificate, token required (see Lan.cs).
    if (lanCert is not null) k.Listen(IPAddress.Any, lanCfg.Port, l => l.UseHttps(lanCert));
});
builder.Logging.ClearProviders();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddProvider(new FileLoggerProvider());
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

builder.Services.AddSingleton(configStore);
builder.Services.AddSingleton<AudioControl>();
builder.Services.AddSingleton<PushStore>();
builder.Services.AddSingleton<CommandRunner>();
builder.Services.AddSingleton<ApprovalStore>();
AddMonitor<SystemMonitor>(builder.Services);
AddMonitor<MediaMonitor>(builder.Services);
AddMonitor<QuotaMonitor>(builder.Services);
AddMonitor<ProcessMonitor>(builder.Services);
AddMonitor<SessionMonitor>(builder.Services);
AddMonitor<GitHubMonitor>(builder.Services);
AddMonitor<PingMonitor>(builder.Services);
AddMonitor<AdbReverse>(builder.Services);
AddMonitor<UpdateService>(builder.Services);
builder.Services.AddHostedService<Tray>();

var app = builder.Build();

// Security:
//  - Local port 8765 listens on 127.0.0.1 only; the Host check blocks DNS rebinding. The phone's USB proxy sends
//    no Origin header; from browsers only the settings page itself (same origin) is accepted (no CSRF).
//  - The optional Wi-Fi port only serves the phone API, only over TLS, and only with the paired token.
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (lanCert is not null && ctx.Connection.LocalPort == lanCfg.Port)
    {
        // The phone's key opens the panel API; the separate webhook key opens only the webhook endpoints, in a header
        // or, for senders that cannot set headers, in ?token= (URLs end up in logs, so it must not be the phone's key).
        var lan = configStore.Get().Lan;
        var authorized = Lan.Matches(ctx.Request.Headers.Authorization, lan.Token!)
            || (Lan.IsWebhook(ctx.Request.Path) && (Lan.Matches(ctx.Request.Headers.Authorization, lan.WebhookToken!)
                || Lan.MatchesQuery(ctx.Request.Query["token"], lan.WebhookToken!)));
        if (origin != "" || !Lan.AllowedOverLan(ctx.Request.Path) || !authorized)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    else
    {
        var host = ctx.Request.Host.Host;
        if ((host != "127.0.0.1" && host != "localhost") || (origin != "" && !SameOrigins.Contains(origin)))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    if (ctx.Request.Path.StartsWithSegments("/api/state") && ctx.Request.Headers.ContainsKey("X-PhoneBox-App"))
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    if (ctx.Request.Headers.TryGetValue("X-PhoneBox-App", out var appVersion) && appVersion.ToString().Length is > 0 and < 32)
        UpdateService.AppVersion = appVersion.ToString();
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});

// ---------------------------------------------------------------- Wi-Fi pairing (local / USB only)
// The phone calls this through the USB tunnel and stores everything it needs to connect over Wi-Fi.
app.MapGet("/api/pair", (ConfigStore cfg) =>
{
    var lan = cfg.Get().Lan;
    if (!lan.Enabled || lanCert is null) return Results.Json(new { enabled = false });
    return Results.Json(new
    {
        enabled = true,
        pc = Environment.MachineName,
        port = lanCfg.Port,
        token = lan.Token,
        fingerprint = Lan.Fingerprint(lanCert),
        hosts = Lan.Addresses(),
    });
});

app.MapGet("/api/lan", (ConfigStore cfg) => Results.Json(new
{
    configured = cfg.Get().Lan.Enabled,
    active = lanCert is not null,
    port = lanCfg.Port,
    restartNeeded = cfg.Get().Lan.Enabled != (lanCert is not null) || (lanCert is not null && cfg.Get().Lan.Port != lanCfg.Port),
    hosts = Lan.Addresses(),
    fingerprint = lanCert is null ? null : Lan.Fingerprint(lanCert),
}));

app.MapGet("/api/update", (UpdateService upd, ConfigStore cfg) => Results.Json(new { status = upd.Status(), auto = cfg.Get().Updates.Auto, installed = UpdateService.RunningInstalled }));
app.MapPost("/api/update/check", async (UpdateService upd) => { await upd.CheckAsync(); return Results.Json(upd.Status()); });
app.MapPost("/api/update/apply", (UpdateService upd) => { _ = Task.Run(() => upd.ApplyAsync()); return Results.Accepted(); });

app.MapPost("/api/restart", (IHostApplicationLifetime life) =>
{
    AppRestart.Requested = true;
    life.StopApplication();
    return Results.Ok();
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

// Widget types and page templates for the settings page's layout editor.
app.MapGet("/api/catalog", () => Results.Text($"{{\"cols\":{Catalog.Cols},\"rows\":{Catalog.Rows},\"widgets\":{Catalog.WidgetsJson},\"templates\":{Catalog.TemplatesJson}}}",
    "application/json; charset=utf-8"));

// ---------------------------------------------------------------- webhook API: push data and events into the panel
// POST /api/push/{key}       body: JSON or plain text (or ?value=); optional ?ttl=seconds. Shown by "push" widgets.
// DELETE /api/push/{key}     removes a value; GET /api/push lists them.
// POST /api/notify           {"title","text","level":"info|ok|warn|error","vibrate":true,"page":"<page id>","seconds":5} or plain text
// POST /api/goto/{page}      switches the phone to a page (id, or 1-based number)
// POST /api/refresh          refreshes quotas/GitHub now and makes the phone reload its settings and data; ?reload=1 reloads the whole panel
app.MapGet("/api/push", (PushStore push) => Results.Json(new { version = push.Version, items = push.Snapshot() }));
app.MapGet("/api/push/{key}", (string key, PushStore push) => push.Get(key) is { } item ? Results.Json(item) : Results.NotFound());
app.MapMethods("/api/push/{key}", ["POST", "PUT"], async (string key, int? ttl, HttpRequest req, PushStore push) =>
{
    var (data, error) = await PushStore.ReadBody(req);
    if (error is not null) return Results.BadRequest(new { error });
    error = push.Set(key, data!.Value, ttl);
    return error is null ? Results.Json(new { ok = true, key, version = push.Version }) : Results.BadRequest(new { error });
});
app.MapDelete("/api/push/{key}", (string key, PushStore push) => push.Remove(key) ? Results.Ok() : Results.NotFound());
app.MapDelete("/api/push", (PushStore push) => { push.Clear(); return Results.Ok(); });

app.MapPost("/api/notify", async (HttpRequest req, PushStore push) =>
{
    var (data, error) = await PushStore.ReadBody(req);
    if (error is not null) return Results.BadRequest(new { error });
    var d = data!.Value;
    var payload = d.ValueKind == JsonValueKind.Object ? d : JsonSerializer.SerializeToElement(new { text = d.ToString() });
    if (!payload.TryGetProperty("text", out _) && !payload.TryGetProperty("title", out _)) return Results.BadRequest(new { error = "give a text or a title" });
    return Results.Json(push.Emit("notify", payload));
});

app.MapPost("/api/goto/{page}", (string page, PushStore push) => Results.Json(push.Emit("goto", JsonSerializer.SerializeToElement(new { page }))));

app.MapPost("/api/refresh", (bool? reload, PushStore push) =>
{
    Wake.Now();
    return Results.Json(push.Emit(reload == true ? "reload" : "refresh"));
});

// ---------------------------------------------------------------- phone panel API
app.MapGet("/api/state", (long? ev, SystemMonitor sys, MediaMonitor media, AudioControl audio, PingMonitor ping, ConfigStore cfg, UpdateService upd, PushStore push) => Results.Json(new
{
    update = upd.Status() is { } u && (u.AppUpdate || u.AgentUpdate) ? new { latest = u.Latest, app = u.AppUpdate, agent = u.AgentUpdate } : null,
    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    host = Environment.MachineName,
    cfgVersion = cfg.Version,
    pushVersion = push.Version,
    evSeq = push.Seq,
    events = ev is { } since ? push.Since(since) : [],
    pc = sys.Snapshot(),
    media = media.Snapshot(),
    audio = audio.Snapshot(),
    ping = ping.Snapshot(),
}));

// Claude Code PermissionRequest hook (local only): waits for Allow / Deny from the phone, prints nothing otherwise.
app.MapPost(ClaudeHook.Endpoint, async (HttpRequest req, ConfigStore cfg, ApprovalStore approvals, SessionMonitor sessions) =>
{
    JsonElement input;
    try { input = (await JsonDocument.ParseAsync(req.Body)).RootElement.Clone(); }
    catch (JsonException) { return Results.Text(""); }
    var output = await ClaudeHook.HandleAsync(input, cfg, approvals, sessions, req.HttpContext.RequestAborted);
    return output is null ? Results.Text("") : Results.Json(output);
});
app.MapGet("/api/claude/hook", () => Results.Json(new { installed = ClaudeHook.Installed(), path = ClaudeHook.SettingsPath }));
app.MapPost("/api/claude/hook", (ConfigStore cfg) => ClaudeHook.Configure(true, Port, cfg.Get().Claude.TimeoutSec) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok());
app.MapDelete("/api/claude/hook", (ConfigStore cfg) => ClaudeHook.Configure(false, Port, cfg.Get().Claude.TimeoutSec) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok());

// Permission prompts waiting for the phone (posted by the Claude Code hook, see ClaudeHook.cs).
app.MapGet("/api/approvals", (ApprovalStore a) => Results.Json(new { items = a.Snapshot() }));
app.MapPost("/api/approvals/{id}", (string id, string decision, ApprovalStore a) => a.Decide(id, decision) ? Results.Ok() : Results.NotFound());

// Output of a command widget (the command itself stays here).
app.MapGet("/api/widget/{page}/{widget}", (string page, string widget, bool? force, CommandRunner runner) =>
    runner.Get(page, widget, force == true) is { } r ? Results.Json(r) : Results.NotFound());

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
    if (!cfg.Get().AllowKill())
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

// Restart requested from the settings page (e.g. Wi-Fi turned on): hand over to a fresh instance.
single.ReleaseMutex();
single.Dispose();
if (AppRestart.Requested && Environment.ProcessPath is { } exe)
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restart") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });

static void AddMonitor<T>(IServiceCollection s) where T : class, IHostedService
{
    s.AddSingleton<T>();
    s.AddHostedService(sp => sp.GetRequiredService<T>());
}
