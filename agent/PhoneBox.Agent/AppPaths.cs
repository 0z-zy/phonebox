using System.Reflection;

namespace PhoneBox.Agent;

/// <summary>File locations. The install folder is replaced on update; settings and logs are kept.</summary>
public static class AppPaths
{
    public const string AppName = "PhoneBox";

    /// <summary>%APPDATA%\PhoneBox — user settings (config.json).</summary>
    public static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    /// <summary>%LOCALAPPDATA%\PhoneBox — logs and the installed binaries.</summary>
    public static readonly string LocalDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    public static readonly string LogPath = Path.Combine(LocalDir, "agent.log");

    public static readonly string StartupShortcut =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>Writes the default settings file on first run.</summary>
    public static void EnsureConfig()
    {
        if (File.Exists(ConfigPath)) return;
        Directory.CreateDirectory(ConfigDir);
        using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("config.default.json")!;
        using var dst = File.Create(ConfigPath);
        src.CopyTo(dst);
    }

    public static Stream? Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    /// <summary>Neutral working directory for child processes, so they never lock the install folder.</summary>
    public static string NeutralCwd => Path.GetTempPath();
}
