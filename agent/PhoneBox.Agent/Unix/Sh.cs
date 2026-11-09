using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>Small helpers for running command-line tools on Linux and macOS.</summary>
public static class Sh
{
    private static readonly Dictionary<string, string?> Found = [];

    /// <summary>Full path of a tool in PATH (or a few usual places), or null.</summary>
    public static string? Which(string tool)
    {
        lock (Found)
        {
            if (Found.TryGetValue(tool, out var hit) && hit is not null && File.Exists(hit)) return hit;
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Concat(["/usr/bin", "/bin", "/usr/local/bin", "/opt/homebrew/bin", "/usr/sbin", "/sbin", "/snap/bin"]);
            hit = dirs.Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists);
            Found[tool] = hit;
            return hit;
        }
    }

    /// <summary>Runs a tool and returns its standard output, or null if it is missing, fails or takes too long.</summary>
    public static string? Run(string tool, params string[] args) => RunWithTimeout(2000, tool, args);

    public static string? RunWithTimeout(int timeoutMs, string tool, params string[] args)
    {
        var path = tool.Contains('/') ? tool : Which(tool);
        if (path is null) return null;
        try
        {
            var psi = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = AppPaths.NeutralCwd };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch { return null; }
    }

    public static string? ReadFile(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint geteuid();

    /// <summary>The effective user id of the agent (to tell the user's own processes from others').</summary>
    public static readonly uint Uid = OperatingSystem.IsWindows() ? 0 : geteuid();
}
