using PhoneBox.Agent;
using Xunit;

public class IcsTests
{
    private static readonly DateTime From = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);   // Monday
    private static readonly DateTime To = From.AddDays(14);

    private static string Cal(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + string.Concat(events.Select(e => "BEGIN:VEVENT\r\n" + e.Trim().Replace("\n", "\r\n") + "\r\nEND:VEVENT\r\n")) + "END:VCALENDAR\r\n";

    private static DateTime Utc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    [Fact]
    public void SingleEventInUtc()
    {
        var e = Ics.Parse(Cal("UID:1\nDTSTART:20261110T090000Z\nDTEND:20261110T093000Z\nSUMMARY:Stand-up\\, team\nLOCATION:Room 1"), From, To).Single();
        Assert.Equal("Stand-up, team", e.Title);
        Assert.Equal("Room 1", e.Location);
        Assert.Equal(new DateTime(2026, 11, 10, 9, 0, 0), Utc(e.Start));
        Assert.Equal(TimeSpan.FromMinutes(30), Utc(e.End) - Utc(e.Start));
        Assert.False(e.AllDay);
    }

    [Fact]
    public void FoldedLinesAreJoined()
    {
        var e = Ics.Parse(Cal("UID:1\nDTSTART:20261110T090000Z\nSUMMARY:A very long\n  title"), From, To).Single();
        Assert.Equal("A very long title", e.Title);
    }

    [Fact]
    public void AllDayEvent()
    {
        var e = Ics.Parse(Cal("UID:1\nDTSTART;VALUE=DATE:20261112\nDTEND;VALUE=DATE:20261113\nSUMMARY:Holiday"), From, To).Single();
        Assert.True(e.AllDay);
        Assert.Equal(TimeSpan.FromDays(1), Utc(e.End) - Utc(e.Start));
    }

    [Fact]
    public void TimeZoneIsApplied()
    {
        // 10:00 in Tallinn in November (UTC+2) is 08:00 UTC
        var e = Ics.Parse(Cal("UID:1\nDTSTART;TZID=Europe/Tallinn:20261110T100000\nDTEND;TZID=Europe/Tallinn:20261110T110000\nSUMMARY:Call"), From, To).Single();
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0), Utc(e.Start));
    }

    [Fact]
    public void WeeklyByDayWithExceptionAndMovedOccurrence()
    {
        var events = Ics.Parse(Cal(
            "UID:w\nDTSTART:20261001T070000Z\nDTEND:20261001T073000Z\nRRULE:FREQ=WEEKLY;BYDAY=MO,TH\nEXDATE:20261112T070000Z\nSUMMARY:Sync",
            "UID:w\nRECURRENCE-ID:20261116T070000Z\nDTSTART:20261116T120000Z\nDTEND:20261116T123000Z\nSUMMARY:Sync (moved)"), From, To).ToList();
        var starts = events.Select(e => (Utc(e.Start), e.Title)).OrderBy(x => x.Item1).ToList();
        Assert.Equal(
        [
            (new DateTime(2026, 11, 9, 7, 0, 0), "Sync"),
            // Thursday the 12th is excluded
            (new DateTime(2026, 11, 16, 12, 0, 0), "Sync (moved)"),
            (new DateTime(2026, 11, 19, 7, 0, 0), "Sync"),
        ], starts);
    }

    [Fact]
    public void DailyStandUpFromYearsAgoStillShows()
    {
        var events = Ics.Parse(Cal("UID:d\nDTSTART:20150105T080000Z\nDTEND:20150105T081500Z\nRRULE:FREQ=DAILY\nSUMMARY:Daily"), From, To).ToList();
        Assert.Equal(14, events.Count);
        Assert.Equal(new DateTime(2026, 11, 9, 8, 0, 0), Utc(events[0].Start));
    }

    [Fact]
    public void CountAndUntilEndTheSeries()
    {
        var counted = Ics.Parse(Cal("UID:c\nDTSTART:20261109T100000Z\nRRULE:FREQ=DAILY;COUNT=3\nSUMMARY:Three"), From, To);
        Assert.Equal(3, counted.Count());
        var until = Ics.Parse(Cal("UID:u\nDTSTART:20261109T100000Z\nRRULE:FREQ=DAILY;UNTIL=20261111T235959Z\nSUMMARY:Until"), From, To);
        Assert.Equal(3, until.Count());
    }

    [Fact]
    public void MonthlySecondTuesday()
    {
        var e = Ics.Parse(Cal("UID:m\nDTSTART:20260113T150000Z\nRRULE:FREQ=MONTHLY;BYDAY=2TU\nSUMMARY:Review"), From, To).Single();
        Assert.Equal(new DateTime(2026, 11, 10, 15, 0, 0), Utc(e.Start));
    }

    [Fact]
    public void CancelledEventsAreSkipped()
    {
        Assert.Empty(Ics.Parse(Cal("UID:x\nDTSTART:20261110T090000Z\nSTATUS:CANCELLED\nSUMMARY:Gone"), From, To));
    }

    [Fact]
    public void WallClockTimeSurvivesDaylightSavingChange()
    {
        // weekly at 09:00 Berlin time across the end of DST (25 Oct 2026): 07:00 UTC before, 08:00 UTC after
        var from = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        var events = Ics.Parse(Cal("UID:b\nDTSTART;TZID=Europe/Berlin:20261019T090000\nRRULE:FREQ=WEEKLY\nSUMMARY:Weekly"), from, from.AddDays(10)).ToList();
        Assert.Equal(new DateTime(2026, 10, 19, 7, 0, 0), Utc(events[0].Start));
        Assert.Equal(new DateTime(2026, 10, 26, 8, 0, 0), Utc(events[1].Start));
    }
}

public class RuleWindowTests
{
    [Theory]
    [InlineData("22:00-07:00", "23:30", true)]
    [InlineData("22:00-07:00", "06:59", true)]
    [InlineData("22:00-07:00", "07:00", false)]
    [InlineData("09:00-17:00", "12:00", true)]
    [InlineData("09:00-17:00", "17:00", false)]
    [InlineData("08:30", "08:30", true)]
    [InlineData("08:30", "08:31", false)]
    [InlineData("nonsense", "08:30", false)]
    public void TimeWindows(string window, string now, bool expected) =>
        Assert.Equal(expected, RuleEngine.InWindow(window, TimeSpan.Parse(now)));
}
