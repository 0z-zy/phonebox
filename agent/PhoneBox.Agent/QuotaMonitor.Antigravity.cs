using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed partial class QuotaMonitor
{
    private static readonly string[] AntigravityProcessNames =
    [
        "agy", "language_server", "language-server", "Antigravity", "Antigravity IDE",
        "language_server_macos", "language_server_macos_arm", "language_server_linux"
    ];

    /// <summary>
    /// Refreshes Antigravity quota and plan data by discovering the local language server process and querying its RPC endpoints.
    /// </summary>
    /// <param name="ct">Cancellation token for async operations.</param>
    /// <returns><c>true</c> if quotas were successfully refreshed; otherwise, <c>false</c>.</returns>
    private async Task<bool> RefreshAntigravity(CancellationToken ct)
    {
        var ep = FindAntigravityEndpoint();
        if (ep is null)
        {
            _antigravity = _antigravity is { Limits.Count: > 0 } prev
                ? prev with { Error = "Antigravity process not running" }
                : null;
            return false;
        }

        try
        {
            var quotaUrl = $"http://127.0.0.1:{ep.Value.Port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
            using var req = new HttpRequestMessage(HttpMethod.Post, quotaUrl);
            req.Headers.Add("X-Codeium-Csrf-Token", ep.Value.CsrfToken);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _antigravity = _antigravity is { Limits.Count: > 0 } prev
                    ? prev with { Error = $"Antigravity HTTP {(int)res.StatusCode}" }
                    : null;
                return false;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var limits = ParseAntigravityLimits(doc.RootElement);
            if (limits.Count == 0)
            {
                _antigravity = _antigravity is { Limits.Count: > 0 } prev
                    ? prev with { Error = "No limits found in Antigravity quota" }
                    : null;
                return false;
            }

            var plan = await GetAntigravityPlanAsync(ep.Value.Port, ep.Value.CsrfToken, ct) ?? _antigravity?.Plan ?? "Antigravity";

            _antigravity = new QuotaProvider("antigravity", "Antigravity", plan, limits,
                null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogDebug("antigravity quota: {Msg}", ex.Message);
            _antigravity = _antigravity is { Limits.Count: > 0 } prev
                ? prev with { Error = ex.Message }
                : null;
            return false;
        }
    }

    /// <summary>
    /// Queries the user status RPC to retrieve the active Antigravity subscription plan name.
    /// </summary>
    /// <param name="port">The loopback port of the Antigravity service.</param>
    /// <param name="csrfToken">The CSRF authentication token.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved subscription plan name, or <c>null</c> if unavailable.</returns>
    private async Task<string?> GetAntigravityPlanAsync(int port, string csrfToken, CancellationToken ct)
    {
        try
        {
            var url = $"http://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUserStatus";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-Codeium-Csrf-Token", csrfToken);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return ParseAntigravityPlan(doc.RootElement);
        }
        catch { return null; }
    }

    /// <summary>
    /// Extracts the subscription plan name from a GetUserStatus response JSON.
    /// </summary>
    /// <param name="root">Root JSON element of the user status response.</param>
    /// <returns>The plan name, or <c>null</c> if not present.</returns>
    internal static string? ParseAntigravityPlan(JsonElement root)
    {
        var u = root.TryGetProperty("userStatus", out var us) ? us : root;
        var planInfo = u.TryGetProperty("planStatus", out var ps) && ps.TryGetProperty("planInfo", out var pi) ? pi : default;
        var name = Str(planInfo, "planName");
        if (string.IsNullOrWhiteSpace(name))
            name = Str(u.TryGetProperty("userTier", out var ut) ? ut : default, "name");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Parses quota groups and bucket limits from a RetrieveUserQuotaSummary response JSON, converting remaining fractions to used percentages.
    /// </summary>
    /// <param name="root">Root JSON element of the quota summary response.</param>
    /// <returns>A list of parsed quota limits ordered by window duration.</returns>
    internal static List<QuotaLimit> ParseAntigravityLimits(JsonElement root)
    {
        var list = new List<QuotaLimit>();
        var resp = root.TryGetProperty("response", out var r) ? r : root;
        if (!resp.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return list;

        var groupCount = groups.GetArrayLength();
        foreach (var g in groups.EnumerateArray())
        {
            var groupName = Str(g, "displayName") ?? "";
            var isGemini = groupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase);
            var isClaudeOrGpt = groupName.Contains("Claude", StringComparison.OrdinalIgnoreCase)
                || groupName.Contains("GPT", StringComparison.OrdinalIgnoreCase);
            var groupPrefix = isGemini ? "Gemini" : isClaudeOrGpt ? "Claude/GPT" : groupName;

            if (!g.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var b in buckets.EnumerateArray())
            {
                var bucketId = Str(b, "bucketId") ?? "";
                var window = Str(b, "window") ?? "";
                var is5h = window == "5h" || bucketId.Contains("5h", StringComparison.OrdinalIgnoreCase);
                var isWeekly = window == "weekly" || bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase);

                string label;
                long? windowSec;
                if (is5h)
                {
                    label = groupCount > 1 ? $"5-hour ({groupPrefix})" : "5-hour session";
                    windowSec = 5 * 3600L;
                }
                else if (isWeekly)
                {
                    label = groupCount > 1 ? $"Weekly ({groupPrefix})" : "Weekly";
                    windowSec = 7 * 86400L;
                }
                else
                {
                    label = Str(b, "displayName") ?? bucketId;
                    windowSec = null;
                }

                var rem = Num(b, "remainingFraction") ?? 1.0;
                var usedPct = Math.Clamp(Math.Round((1.0 - rem) * 100.0, 1), 0.0, 100.0);
                var resetsAt = Iso(Str(b, "resetTime"));
                var severity = usedPct >= 95 ? "critical" : usedPct >= 80 ? "warning" : "normal";

                list.Add(new QuotaLimit(label, usedPct, resetsAt, windowSec, severity));
            }
        }

        return list
            .OrderBy(l => l.Label.Contains("Claude", StringComparison.OrdinalIgnoreCase) || l.Label.Contains("GPT", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(l => l.WindowSec ?? long.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Extracts the loopback port and CSRF token flags from a process command line.
    /// </summary>
    /// <param name="cmd">The command line string.</param>
    /// <returns>A tuple containing the port and CSRF token, or <c>null</c> if not found.</returns>
    internal static (int Port, string CsrfToken)? ParseEndpointFromCommandLine(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var portMatch = AntigravityPortRegex().Match(cmd);
        var csrfMatch = AntigravityCsrfRegex().Match(cmd);
        if (portMatch.Success && csrfMatch.Success && int.TryParse(portMatch.Groups["port"].Value, out var port))
            return (port, csrfMatch.Groups["csrf"].Value);
        return null;
    }

    /// <summary>
    /// Scans running processes for an active Antigravity instance and extracts its port and CSRF token.
    /// </summary>
    /// <returns>The discovered endpoint tuple, or <c>null</c> if no active process was found.</returns>
    private static (int Port, string CsrfToken)? FindAntigravityEndpoint()
    {
        foreach (var name in AntigravityProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                using (p)
                {
                    try
                    {
                        var cmd = GetCommandLine(p.Id);
                        if (string.IsNullOrEmpty(cmd)) continue;
                        var ep = ParseEndpointFromCommandLine(cmd);
                        if (ep is not null) return ep;
                    }
                    catch { }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Retrieves the command-line arguments for a process across supported operating systems.
    /// </summary>
    /// <param name="pid">The process identifier.</param>
    /// <returns>The command-line string, or <c>null</c> if unreadable.</returns>
    private static string? GetCommandLine(int pid)
    {
        if (OperatingSystem.IsWindows())
            return GetWindowsCommandLine(pid);
        if (OperatingSystem.IsLinux())
        {
            try { return File.Exists($"/proc/{pid}/cmdline") ? File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim() : null; }
            catch { return null; }
        }
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("ps", $"-p {pid} -o args=")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return p?.StandardOutput.ReadToEnd().Trim();
            }
            catch { return null; }
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint h, int infoClass, nint info, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint h);

    /// <summary>
    /// Reads the process command line on Windows using NtQueryInformationProcess.
    /// </summary>
    /// <param name="pid">The target process identifier.</param>
    /// <returns>The command line string, or <c>null</c> if query fails.</returns>
    private static string? GetWindowsCommandLine(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            NtQueryInformationProcess(h, 60 /* ProcessCommandLineInformation */, 0, 0, out var len);
            if (len <= 0 || len > 1 << 20) return null;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (NtQueryInformationProcess(h, 60, buf, len, out _) != 0) return null;
                var chars = Marshal.ReadInt16(buf) / 2;
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, IntPtr.Size), chars);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Matches port arguments such as --hub-port, --port, or --hub_port.
    /// </summary>
    [GeneratedRegex(@"(?:--hub-port|--port|--hub_port)[=\s]+[""']?(?<port>\d+)[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex AntigravityPortRegex();

    /// <summary>
    /// Matches CSRF token arguments such as --csrf_token or --csrfToken.
    /// </summary>
    [GeneratedRegex(@"(?:--csrf_token|--csrfToken)[=\s]+[""']?(?<csrf>[^\s""']+)[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex AntigravityCsrfRegex();
}
