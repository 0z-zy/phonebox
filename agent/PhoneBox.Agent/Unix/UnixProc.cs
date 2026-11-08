using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>Process details on Linux (/proc) and macOS (ps): executable, arguments, owner, a friendly name and an icon.</summary>
public static class UnixProc
{
    public static readonly HashSet<string> Protected = new(StringComparer.Ordinal)
    {
        // Linux
        "systemd", "init", "kthreadd", "Xorg", "Xwayland", "gnome-shell", "gnome-session-binary", "plasmashell", "kwin_x11", "kwin_wayland",
        "mutter", "pipewire", "pipewire-pulse", "wireplumber", "pulseaudio", "dbus-daemon", "dbus-broker", "sshd", "login", "gdm", "sddm", "lightdm",
        "NetworkManager", "polkitd", "udisksd", "upowerd",
        // macOS
        "kernel_task", "launchd", "WindowServer", "loginwindow", "Dock", "Finder", "SystemUIServer", "coreaudiod", "mds", "mds_stores",
        "cfprefsd", "distnoted", "ControlCenter", "NotificationCenter",
        // ours
        "PhoneBoxAgent", "adb", "dotnet",
    };

    private record PsRow(uint Uid, string Path, string Args);
    private static Dictionary<int, PsRow> _ps = [];
    private static long _psAt;
    private static readonly object PsLock = new();

    /// <summary>macOS: owner, executable and arguments of every process from two <c>ps</c> calls, cached for two seconds
    /// (one <c>ps</c> per process takes seconds with a few hundred processes).</summary>
    private static Dictionary<int, PsRow> Ps()
    {
        lock (PsLock)
        {
            if (Environment.TickCount64 - _psAt < 2000) return _ps;
            var map = new Dictionary<int, PsRow>();
            var args = new Dictionary<int, string>();
            foreach (var line in (Sh.Run("ps", "-axww", "-o", "pid=,args=") ?? "").Split('\n'))
            {
                var t = line.Trim(); var i = t.IndexOf(' ');
                if (i > 0 && int.TryParse(t[..i], out var pid)) args[pid] = t[(i + 1)..].Trim();
            }
            foreach (var line in (Sh.Run("ps", "-axww", "-o", "pid=,uid=,comm=") ?? "").Split('\n'))
            {
                var f = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length == 3 && int.TryParse(f[0], out var pid) && uint.TryParse(f[1], out var uid))
                    map[pid] = new PsRow(uid, f[2].Trim(), args.GetValueOrDefault(pid, ""));
            }
            _ps = map;
            _psAt = Environment.TickCount64;
            return map;
        }
    }

    /// <summary>True if the process belongs to the user running the agent (only those may be ended).</summary>
    public static bool IsOwn(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            var status = Sh.ReadFile($"/proc/{pid}/status");
            var uidLine = status?.Split('\n').FirstOrDefault(l => l.StartsWith("Uid:"));
            return uidLine is not null && uint.TryParse(uidLine.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries)[1], out var uid) && uid == Sh.Uid;
        }
        return Ps().TryGetValue(pid, out var row) && row.Uid == Sh.Uid;
    }

    /// <summary>The parent's PID: /proc/&lt;pid&gt;/stat on Linux (the field after the command name), ps on macOS.</summary>
    public static int? ParentPid(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            var stat = Sh.ReadFile($"/proc/{pid}/stat");
            var close = stat?.LastIndexOf(')') ?? -1; // the command name may contain spaces and parentheses
            var f = close < 0 ? [] : stat![(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return f.Length > 1 && int.TryParse(f[1], out var pp) ? pp : null;
        }
        lock (PsLock)
        {
            if (Environment.TickCount64 - _ppidAt >= 2000) // one ps call for the whole tree walk
            {
                var map = new Dictionary<int, int>();
                foreach (var line in (Sh.Run("ps", "-axo", "pid=,ppid=") ?? "").Split('\n'))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length == 2 && int.TryParse(f[0], out var p) && int.TryParse(f[1], out var pp)) map[p] = pp;
                }
                _ppid = map;
                _ppidAt = Environment.TickCount64;
            }
            return _ppid.TryGetValue(pid, out var parent) ? parent : null;
        }
    }

    private static Dictionary<int, int> _ppid = [];
    private static long _ppidAt = -10_000;

    public static string? ImagePath(Process p)
    {
        if (OperatingSystem.IsLinux())
        {
            try { return new FileInfo($"/proc/{p.Id}/exe").LinkTarget; } catch { return null; }
        }
        return Ps().TryGetValue(p.Id, out var row) && row.Path.Length > 0 ? row.Path : null;
    }

    public static string? Args(int pid)
    {
        string? cmd = OperatingSystem.IsLinux()
            ? Sh.ReadFile($"/proc/{pid}/cmdline")?.Replace('\0', ' ').Trim()
            : Ps().GetValueOrDefault(pid)?.Args;
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var i = cmd.IndexOf(' ');
        var rest = i > 0 ? cmd[(i + 1)..].Trim() : "";
        return rest.Length == 0 ? null : rest.Length > 140 ? rest[..137] + "…" : rest;
    }

    /// <summary>"Visual Studio Code" for /Applications/Visual Studio Code.app/…, otherwise the process name.</summary>
    public static string Title(string name, string? path)
    {
        if (path is not null && path.Contains(".app/"))
        {
            var app = path[..path.IndexOf(".app/")];
            return Path.GetFileName(app);
        }
        if (OperatingSystem.IsLinux() && DesktopName(name) is { } n) return n;
        return name;
    }

    private static readonly Lazy<Dictionary<string, (string Name, string? Icon)>> Desktop = new(ReadDesktopFiles);

    private static string? DesktopName(string exe) => Desktop.Value.TryGetValue(exe, out var d) ? d.Name : null;

    /// <summary>Linux: maps executables to the Name and Icon of their .desktop launcher.</summary>
    private static Dictionary<string, (string, string?)> ReadDesktopFiles()
    {
        var map = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[] { "/usr/share/applications", "/usr/local/share/applications", "/var/lib/snapd/desktop/applications",
                     "/var/lib/flatpak/exports/share/applications", Path.Combine(home, ".local/share/applications") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.desktop"))
            {
                try
                {
                    string? name = null, exec = null, icon = null;
                    foreach (var line in File.ReadLines(file))
                    {
                        if (line.StartsWith('[') && line != "[Desktop Entry]" && name is not null) break;
                        if (name is null && line.StartsWith("Name=")) name = line[5..];
                        else if (exec is null && line.StartsWith("Exec=")) exec = line[5..];
                        else if (icon is null && line.StartsWith("Icon=")) icon = line[5..];
                    }
                    if (name is null || exec is null) continue;
                    var bin = Path.GetFileName(exec.Trim('"').Split(' ')[0].Trim('"'));
                    if (bin.Length > 0 && bin.Length <= 15) map.TryAdd(bin.Length > 15 ? bin[..15] : bin, (name, icon));
                    map.TryAdd(bin, (name, icon));
                }
                catch { }
            }
        }
        return map;
    }

    /// <summary>A PNG icon for the process, if one can be found (Linux icon themes, macOS app bundles).</summary>
    public static byte[]? Icon(string name, string? path)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var icon = Desktop.Value.TryGetValue(name, out var d) ? d.Icon : null;
                icon ??= name;
                if (icon.StartsWith('/') && icon.EndsWith(".png") && File.Exists(icon)) return File.ReadAllBytes(icon);
                foreach (var size in new[] { "64x64", "48x48", "128x128", "256x256", "32x32" })
                    foreach (var root in new[] { "/usr/share/icons/hicolor", "/usr/share/icons/Adwaita" })
                    {
                        var f = Path.Combine(root, size, "apps", icon + ".png");
                        if (File.Exists(f)) return File.ReadAllBytes(f);
                    }
                var px = Path.Combine("/usr/share/pixmaps", icon + ".png");
                return File.Exists(px) ? File.ReadAllBytes(px) : null;
            }
            if (OperatingSystem.IsMacOS() && path is not null && path.Contains(".app/"))
            {
                var app = path[..(path.IndexOf(".app/") + 4)];
                var res = Path.Combine(app, "Contents", "Resources");
                var icns = Directory.Exists(res) ? Directory.EnumerateFiles(res, "*.icns").OrderBy(f => f.Length).FirstOrDefault() : null;
                if (icns is null) return null;
                var png = Path.Combine(Path.GetTempPath(), $"phonebox-icon-{name.GetHashCode():x}.png");
                Sh.Run("sips", "-s", "format", "png", "--resampleWidth", "64", icns, "--out", png);
                return File.Exists(png) ? File.ReadAllBytes(png) : null;
            }
        }
        catch { }
        return null;
    }
}
