using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>
/// Linux / macOS: no tray icon (the agent usually runs as a systemd user service or a launchd agent). The settings live
/// in the browser at http://127.0.0.1:8765; this just says so in the log.
/// </summary>
public sealed class Tray(ILogger<Tray> log, AgentEndpoint endpoint) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        log.LogInformation("settings: {Url}  (no tray icon on this system; run PhoneBoxAgent again to open the settings)", endpoint.SettingsUrl);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Windows ties helper processes to the agent with a job object; elsewhere they are stopped on shutdown.</summary>
public static class JobObject
{
    public static void Track(Process p) { }
}
