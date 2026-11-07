using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record GpuInfo(string Name, double? Util, double? Temp, double? Power, double? PowerLimit,
    double? VramUsed, double? VramTotal, double? Fan, double? Clock);

public sealed record DiskInfo(string Name, double UsedGb, double TotalGb);

public sealed record PcSnapshot(double? Cpu, double? CpuTemp, string CpuName, double RamUsedGb, double RamTotalGb,
    GpuInfo? Gpu, double NetDownBps, double NetUpBps, IReadOnlyList<DiskInfo> Disks, long UptimeSec);

/// <summary>Samples CPU, RAM, network, disk and NVIDIA GPU metrics once a second.</summary>
public sealed class SystemMonitor(ILogger<SystemMonitor> log) : BackgroundService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(600) };
    private readonly string _cpuName = ReadCpuName();

    private volatile PcSnapshot? _snap;
    private volatile GpuInfo? _gpu;
    private double? _cpuTemp;
    private IReadOnlyList<DiskInfo> _disks = [];

    public PcSnapshot? Snapshot() => _snap;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = Task.Run(() => GpuLoop(ct), ct);
        _ = Task.Run(() => CpuTempLoop(ct), ct);

        GetSystemTimes(out var idle0, out var kernel0, out var user0);
        var (rx0, tx0) = NetBytes();
        var t0 = Stopwatch.GetTimestamp();
        var tick = 0;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            GetSystemTimes(out var idle1, out var kernel1, out var user1);
            var total = (kernel1 - kernel0) + (user1 - user0);
            double? cpu = total > 0 ? Math.Clamp(100.0 * (total - (idle1 - idle0)) / total, 0, 100) : null;
            (idle0, kernel0, user0) = (idle1, kernel1, user1);

            var (rx1, tx1) = NetBytes();
            var t1 = Stopwatch.GetTimestamp();
            var secs = Math.Max(0.001, (t1 - t0) / (double)Stopwatch.Frequency);
            var down = Math.Max(0, (rx1 - rx0) / secs);
            var up = Math.Max(0, (tx1 - tx0) / secs);
            (rx0, tx0, t0) = (rx1, tx1, t1);

            if (tick++ % 15 == 0) _disks = ReadDisks();

            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref mem);
            const double Gb = 1024d * 1024 * 1024;

            _snap = new PcSnapshot(
                Cpu: cpu is { } c ? Math.Round(c, 1) : null,
                CpuTemp: _cpuTemp,
                CpuName: _cpuName,
                RamUsedGb: Math.Round((mem.ullTotalPhys - mem.ullAvailPhys) / Gb, 1),
                RamTotalGb: Math.Round(mem.ullTotalPhys / Gb, 1),
                Gpu: _gpu,
                NetDownBps: Math.Round(down),
                NetUpBps: Math.Round(up),
                Disks: _disks,
                UptimeSec: Environment.TickCount64 / 1000);
        }
    }

    // Keeps nvidia-smi running and reads one line per second.
    private async Task GpuLoop(CancellationToken ct)
    {
        var name = "GPU";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                name = (await RunAsync("nvidia-smi", "--query-gpu=name --format=csv,noheader", ct)).Trim().Split('\n')[0].Trim();
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=utilization.gpu,temperature.gpu,power.draw,power.limit,memory.used,memory.total,fan.speed,clocks.gr " +
                    "--format=csv,noheader,nounits -lms 1000")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.NeutralCwd };
                using var p = Process.Start(psi)!;
                JobObject.Track(p);
                using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
                while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    var f = line.Split(',').Select(s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null).ToArray();
                    if (f.Length < 8) continue;
                    _gpu = new GpuInfo(name, f[0], f[1], f[2], f[3], f[4] / 1024, f[5] / 1024, f[6], f[7]);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogDebug("nvidia-smi: {Msg}", ex.Message); }
            _gpu = null;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    // CPU temperature comes from LibreHardwareMonitor's web server (port 8085), when it is running.
    private async Task CpuTempLoop(CancellationToken ct)
    {
        string[] names = ["Core (Tctl/Tdie)", "Tctl/Tdie", "CPU Package", "Tdie", "Tctl", "Core (Tdie)"];
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(2);
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync("http://127.0.0.1:8085/data.json", ct));
                _cpuTemp = FindTemp(doc.RootElement, names);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch
            {
                _cpuTemp = null;
                delay = TimeSpan.FromSeconds(30);
            }
            await Task.Delay(delay, ct);
        }
    }

    private static double? FindTemp(JsonElement node, string[] names)
    {
        if (node.TryGetProperty("Text", out var text) && names.Contains(text.GetString())
            && node.TryGetProperty("Value", out var val) && val.GetString() is { } s && s.Contains('°'))
        {
            var num = s.Split(' ')[0].Replace(',', '.');
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) return t;
        }
        if (node.TryGetProperty("Children", out var kids))
            foreach (var k in kids.EnumerateArray())
                if (FindTemp(k, names) is { } t) return t;
        return null;
    }

    private static (long rx, long tx) NetBytes()
    {
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
            var d = ni.Description + " " + ni.Name;
            if (d.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || d.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || d.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) || d.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || d.Contains("WSL", StringComparison.OrdinalIgnoreCase) || d.Contains("TAP", StringComparison.Ordinal)
                || d.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            catch { }
        }
        return (rx, tx);
    }

    private static IReadOnlyList<DiskInfo> ReadDisks()
    {
        const double Gb = 1024d * 1024 * 1024;
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DiskInfo(d.Name.TrimEnd('\\'), Math.Round((d.TotalSize - d.TotalFreeSpace) / Gb), Math.Round(d.TotalSize / Gb)))
            .ToList();
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    private static async Task<string> RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.NeutralCwd };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return output;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
