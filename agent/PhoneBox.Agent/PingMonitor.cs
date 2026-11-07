using System.Net.NetworkInformation;

namespace PhoneBox.Agent;

public sealed record PingSnapshot(string Host, double? Ms, double? AvgMs, double LossPct);

/// <summary>Measures internet latency (1.1.1.1) every 5 s; reports the 5-minute average and packet loss.</summary>
public sealed class PingMonitor : BackgroundService
{
    private const string Host = "1.1.1.1";
    private readonly Queue<double?> _samples = new();
    private volatile PingSnapshot? _snap;

    public PingSnapshot? Snapshot() => _snap;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            double? ms = null;
            try
            {
                var r = await ping.SendPingAsync(Host, 2000);
                if (r.Status == IPStatus.Success) ms = r.RoundtripTime;
            }
            catch { }

            _samples.Enqueue(ms);
            while (_samples.Count > 60) _samples.Dequeue();
            var ok = _samples.Where(s => s is not null).Select(s => s!.Value).ToList();
            _snap = new PingSnapshot(Host, ms, ok.Count > 0 ? Math.Round(ok.Average(), 1) : null,
                Math.Round(100.0 * (_samples.Count - ok.Count) / _samples.Count, 1));
            await Task.Delay(5000, ct);
        }
    }
}
