namespace PhoneBox.Agent;

/// <summary>A separate local port allows development checks beside an installed agent.</summary>
public sealed record AgentEndpoint(int Port = 8765)
{
    public string SettingsUrl => $"http://127.0.0.1:{Port}/";
    public string MutexName => Port == 8765 ? @"Local\PhoneBox.Agent" : $@"Local\PhoneBox.Agent.{Port}";

    public static AgentEndpoint FromArgs(string[] args)
    {
        var positions = args.Select((arg, i) => (arg, i)).Where(x => x.arg == "--port").ToArray();
        if (positions.Length == 0) return new();
        var index = positions[0].i;
        if (positions.Length != 1 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var port) || port is < 1024 or > 65535)
            throw new ArgumentException("--port requires one integer between 1024 and 65535");
        return new(port);
    }
}
