using PhoneBox.Agent;
using Xunit;

public class BatteryTests
{
    [Theory]
    [InlineData("WH-1000XM5", "headphones")]
    [InlineData("Galaxy Buds2 Pro", "headphones")]
    [InlineData("AirPods Pro", "headphones")]
    [InlineData("MX Master 3S", "mouse")]
    [InlineData("Logitech Pebble M350", "mouse")]
    [InlineData("MX Keys", "keyboard")]
    [InlineData("Slim Solar+", "keyboard")]
    [InlineData("Xbox Wireless Controller", "controller")]
    [InlineData("DualSense Wireless Controller", "controller")]
    [InlineData("Some Speaker", "other")]
    public void KindFromName(string name, string kind) => Assert.Equal(kind, BatteryMonitor.KindOf(name));
}
