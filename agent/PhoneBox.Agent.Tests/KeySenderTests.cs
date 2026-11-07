using PhoneBox.Agent;
using Xunit;

public class KeySenderTests
{
    [Fact]
    public void ParsesCombosAndSequences()
    {
        var combos = KeySender.Parse("Ctrl+Shift+M, cmd+Space,F13, play");
        Assert.Equal([["ctrl", "shift", "m"], ["win", "space"], ["f13"], ["playpause"]], combos);
    }

    [Theory]
    [InlineData("ctrl+a+b")]      // two keys besides modifiers
    [InlineData("ctrl+banana")]   // unknown key
    [InlineData("f25")]
    [InlineData(" , ")]           // nothing
    public void RejectsBadSpecs(string spec) => Assert.Throws<ArgumentException>(() => KeySender.Parse(spec));

    [Fact]
    public void ModifiersAloneAreAllowed() => Assert.Equal([["win"]], KeySender.Parse("Win"));
}
