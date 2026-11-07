using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>Rule conditions (days, hours, pushed value comparisons) and the charge guard's decisions.</summary>
public class RuleTests
{
    [Theory]
    [InlineData(null, DayOfWeek.Sunday, true)]
    [InlineData("mon,tue,wed,thu,fri", DayOfWeek.Friday, true)]
    [InlineData("mon,tue,wed,thu,fri", DayOfWeek.Saturday, false)]
    [InlineData("sat,sun", DayOfWeek.Sunday, true)]
    public void Days(string? days, DayOfWeek day, bool expected) => Assert.Equal(expected, RuleEngine.OnDay(days, day));

    [Theory]
    [InlineData("weekdays", "mon,tue,wed,thu,fri")]
    [InlineData("Sun, sat", "sat,sun")]
    [InlineData("weekdays weekends", null)]   // every day
    [InlineData("funday", null)]
    [InlineData("", null)]
    public void DayListsAreCleaned(string input, string? expected) => Assert.Equal(expected, RuleEngine.NormalizeDays(input));

    [Fact]
    public void DaysAndHoursCombine()
    {
        var r = new RuleConfig { Days = "mon,tue,wed,thu,fri", During = "09:00-18:00" };
        Assert.True(RuleEngine.Allowed(r, new DateTime(2026, 11, 9, 10, 0, 0)));   // Monday 10:00
        Assert.False(RuleEngine.Allowed(r, new DateTime(2026, 11, 9, 19, 0, 0)));  // Monday 19:00
        Assert.False(RuleEngine.Allowed(r, new DateTime(2026, 11, 8, 10, 0, 0)));  // Sunday
        Assert.True(RuleEngine.Allowed(new RuleConfig(), new DateTime(2026, 11, 8, 3, 0, 0)));
    }

    [Theory]
    [InlineData("29.5", ">", "28", true)]
    [InlineData("27", ">", "28", false)]
    [InlineData("\"12\"", "<", "20", true)]          // a number sent as text
    [InlineData("\"open\"", "=", "OPEN", true)]
    [InlineData("\"closed\"", "!=", "open", true)]
    [InlineData("{\"value\":31,\"sub\":\"living room\"}", ">", "30", true)]
    [InlineData("\"warm\"", ">", "20", false)]       // not a number
    public void PushedValuesCompare(string json, string op, string arg, bool expected) =>
        Assert.Equal(expected, RuleEngine.Compare(JsonDocument.Parse(json).RootElement, op, arg));

    [Fact]
    public void RuleOptionsAreClamped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "rules": [ { "when": "always", "then": "shortcut", "priority": 42, "forSec": -5, "cooldownMin": 99999, "countdownSec": 99, "days": "weekdays", "op": "~", "during": "  " } ] }""");
        try
        {
            var r = new ConfigStore(path).Get().Rules.Single();
            Assert.Equal(9, r.Priority);
            Assert.Equal(0, r.ForSec);
            Assert.Equal(1440, r.CooldownMin);
            Assert.Equal(30, r.CountdownSec);
            Assert.Equal("mon,tue,wed,thu,fri", r.Days);
            Assert.Null(r.Op);
            Assert.Null(r.During);
        }
        finally { File.Delete(path); }
    }
}

public class ChargeGuardTests
{
    private static readonly ChargeConfig C = new() { Enabled = true, Min = 50, Max = 80 };
    private const long Now = 1_000_000_000;
    private static PhoneBatteryReport At(int level, bool plugged, long ageMs = 0) => new(level, plugged, null, Now - ageMs);

    [Fact]
    public void StopsChargingAtTheTop() => Assert.Equal("off", ChargeGuard.Decide(C, At(80, true), Now, null, 0));

    [Fact]
    public void StartsChargingAtTheBottom() => Assert.Equal("on", ChargeGuard.Decide(C, At(50, false), Now, "off", Now - 60_000));

    [Fact]
    public void LeavesThePowerAloneInBetween()
    {
        Assert.Null(ChargeGuard.Decide(C, At(65, true), Now, "on", Now - 60_000));
        Assert.Null(ChargeGuard.Decide(C, At(65, false), Now, "off", Now - 60_000));
    }

    [Fact]
    public void RepeatsASwitchThatDidNotTakeOnlyAfterAWhile()
    {
        Assert.Null(ChargeGuard.Decide(C, At(85, true), Now, "off", Now - 60_000));             // just switched: wait
        Assert.Equal("off", ChargeGuard.Decide(C, At(85, true), Now, "off", Now - 11 * 60_000)); // still charging later: again
    }

    [Fact]
    public void NeverLeavesASilentPhoneWithoutPower()
    {
        Assert.Equal("on", ChargeGuard.Decide(C, At(70, false, ageMs: 11 * 60_000), Now, "off", Now - 20 * 60_000));
        Assert.Equal("on", ChargeGuard.Decide(C, null, Now, "off", 0));
        Assert.Null(ChargeGuard.Decide(C, null, Now, "on", 0));
    }

    [Fact]
    public void LimitsAreKeptApart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "charge": { "enabled": true, "min": 70, "max": 60, "method": "magic" } }""");
        try
        {
            var c = new ConfigStore(path).Get().Charge;
            Assert.Equal(70, c.Min);
            Assert.Equal(75, c.Max);
            Assert.Equal("ha", c.Method);
        }
        finally { File.Delete(path); }
    }
}
