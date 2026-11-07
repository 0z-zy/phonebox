using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>Runs the shortcuts defined in config.json. The phone only sends a shortcut id, never a command.</summary>
public static class Shortcuts
{
    /// <returns>An error message, or null on success.</returns>
    public static string? Run(AgentConfig cfg, string id, ILogger log)
    {
        var sc = cfg.Shortcuts.FirstOrDefault(s => s.Id == id);
        return sc is null ? $"no such shortcut: {id}" : Execute(sc, log);
    }

    /// <returns>An error message, or null on success.</returns>
    public static string? Execute(Shortcut sc, ILogger log)
    {
        try
        {
            switch (sc.Cmd)
            {
                case "builtin:lock":
                    LockWorkStation();
                    break;
                case "builtin:monitor-off":
                    // HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2 = off
                    PostMessage(0xFFFF, 0x0112, 0xF170, 2);
                    break;
                case "builtin:mute":
                    Keys.Tap(Keys.VK_VOLUME_MUTE);
                    break;
                default:
                    var psi = new ProcessStartInfo(sc.Cmd, sc.Args ?? "") { UseShellExecute = true };
                    if (!string.IsNullOrEmpty(sc.Cwd)) psi.WorkingDirectory = sc.Cwd;
                    Process.Start(psi);
                    break;
            }
            log.LogInformation("shortcut: {Label}", sc.Label);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning("shortcut {Label} failed: {Msg}", sc.Label, ex.Message);
            return ex.Message;
        }
    }

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
}

public static class Keys
{
    public const byte VK_VOLUME_MUTE = 0xAD;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

    public static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, 0);
        keybd_event(vk, 0, 2 /* KEYEVENTF_KEYUP */, 0);
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
}
