using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>
/// Claude Code integration: a PermissionRequest hook in ~/.claude/settings.json posts each permission prompt to the
/// agent, which shows it on the phone and returns Allow / Deny as the hook's output. No answer means no output, and
/// Claude Code asks in the terminal as usual.
/// </summary>
public static class ClaudeHook
{
    public const string Endpoint = "/api/hooks/claude/permission";
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string SettingsPath = Path.Combine(Home, ".claude", "settings.json");

    /// <summary>Handles one hook call; returns the JSON for the hook to print, or null for "no decision".</summary>
    public static async Task<object?> HandleAsync(JsonElement input, ConfigStore cfg, ApprovalStore approvals, SessionMonitor sessions, CancellationToken aborted)
    {
        var c = cfg.Get().Claude;
        if (!c.PhoneApprovals) return null;
        if (c.OnlyWhenAway && Idle.Seconds() < 60) return null;

        var tool = Str(input, "tool_name") ?? "a tool";
        var ti = input.TryGetProperty("tool_input", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
        var cwd = Str(input, "cwd") ?? "";
        var sessionId = Str(input, "session_id") ?? "";
        var session = sessions.Snapshot().FirstOrDefault(s => s.Id == "claude:" + sessionId);
        var project = session?.Name ?? Path.GetFileName(cwd.TrimEnd('\\', '/'));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var a = new Approval(ApprovalStore.NewId(), "Claude Code", sessionId, project, ToolTitle(tool), Detail(tool, ti),
            ti.ValueKind == JsonValueKind.Object ? Str(ti, "description") : null, now, now + Math.Clamp(c.TimeoutSec, 10, 3600) * 1000L);

        var decision = await approvals.AskAsync(a, aborted);
        if (decision is null) return null;
        return new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PermissionRequest",
                decision = decision == "allow"
                    ? (object)new { behavior = "allow" }
                    : new { behavior = "deny", message = "Denied from the PhoneBox panel." },
            },
        };
    }

    private static string ToolTitle(string tool) => tool switch
    {
        "Bash" => "Run a command",
        "Edit" or "MultiEdit" => "Edit a file",
        "Write" => "Write a file",
        "NotebookEdit" => "Edit a notebook",
        "WebFetch" => "Fetch a web page",
        "WebSearch" => "Search the web",
        "Read" => "Read a file",
        _ when tool.StartsWith("mcp__") => "Use " + tool[5..].Replace("__", " › "),
        _ => "Use " + tool,
    };

    private static string Detail(string tool, JsonElement ti)
    {
        if (ti.ValueKind != JsonValueKind.Object) return "";
        string? s = tool switch
        {
            "Bash" => Str(ti, "command"),
            "Edit" or "MultiEdit" or "Read" or "NotebookEdit" => Str(ti, "file_path") ?? Str(ti, "notebook_path"),
            "Write" => Str(ti, "file_path") is { } f ? f + "\n\n" + Head(Str(ti, "content") ?? "", 12) : null,
            "WebFetch" => Str(ti, "url"),
            "WebSearch" => Str(ti, "query"),
            _ => null,
        };
        if (tool is "Edit" && Str(ti, "new_string") is { } ns) s += "\n\n" + Head(ns, 12);
        s ??= JsonSerializer.Serialize(ti, new JsonSerializerOptions { WriteIndented = true });
        return s.Length > 3000 ? s[..3000] + "…" : s;
    }

    private static string Head(string text, int lines)
    {
        var l = text.Split('\n');
        return string.Join('\n', l.Take(lines)) + (l.Length > lines ? $"\n… ({l.Length - lines} more lines)" : "");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------------------------------------------------------------- install / remove the hook in ~/.claude/settings.json

    private static string HookCommand(int port) =>
        $"{(OperatingSystem.IsWindows() ? "curl.exe" : "curl")} -s --max-time 3600 -X POST http://127.0.0.1:{port}{Endpoint} -H \"Content-Type: application/json\" --data-binary \"@-\"";

    private static bool IsOurs(JsonNode? hook) => hook?["command"]?.GetValue<string>()?.Contains(Endpoint) == true;

    public static bool Installed()
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(SettingsPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return root?["hooks"]?["PermissionRequest"]?.AsArray().Any(g => g?["hooks"]?.AsArray().Any(IsOurs) == true) == true;
        }
        catch { return false; }
    }

    /// <summary>Adds (or, with install = false, removes) the PhoneBox hook, keeping everything else and a backup.</summary>
    public static string? Configure(bool install, int port, int timeoutSec)
    {
        try
        {
            JsonObject root;
            if (File.Exists(SettingsPath))
            {
                var text = File.ReadAllText(SettingsPath);
                root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                       ?? throw new InvalidDataException("settings.json is not a JSON object");
                File.WriteAllText(SettingsPath + ".phonebox.bak", text);
            }
            else root = new JsonObject();

            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            var groups = hooks["PermissionRequest"] as JsonArray ?? new JsonArray();
            // drop our old entries (and groups that become empty)
            foreach (var g in groups.ToList())
            {
                if (g?["hooks"] is not JsonArray hs) continue;
                foreach (var h in hs.Where(IsOurs).ToList()) hs.Remove(h);
                if (hs.Count == 0) groups.Remove(g);
            }
            if (install)
                groups.Add(new JsonObject
                {
                    ["matcher"] = "*",
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = HookCommand(port),
                        ["timeout"] = Math.Clamp(timeoutSec, 10, 3600) + 15,
                    }),
                });
            if (groups.Count > 0) hooks["PermissionRequest"] = groups; else hooks.Remove("PermissionRequest");
            if (hooks.Count > 0) root["hooks"] = hooks; else root.Remove("hooks");

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, SettingsPath, overwrite: true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}

/// <summary>How long the PC's user has not touched the keyboard or mouse.</summary>
public static class Idle
{
    public static double Seconds()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? (Environment.TickCount - (int)info.dwTime) / 1000.0 : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
