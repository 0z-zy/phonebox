using System.Collections.Concurrent;

namespace PhoneBox.Agent;

public sealed record PhoneSeen(string Id, string Model, long LastSeen, string Via);

/// <summary>
/// The phones that talk to this agent (they send X-PhoneBox-Device / X-PhoneBox-Model), so the settings can give each
/// one its own pages. Remembered while the agent runs.
/// </summary>
public static class PhoneRegistry
{
    private static readonly ConcurrentDictionary<string, PhoneSeen> Seen = new();

    public static void Touch(string? id, string? model, string via)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || !id.All(char.IsAsciiLetterOrDigit)) return;
        model = string.IsNullOrWhiteSpace(model) ? "Android phone" : new string(model.Where(c => c >= ' ' && c <= '~').Take(40).ToArray());
        Seen[id] = new PhoneSeen(id, model, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), via);
    }

    public static IReadOnlyCollection<PhoneSeen> All() => Seen.Values.ToList();
}
