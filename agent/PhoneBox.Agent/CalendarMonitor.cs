using System.Globalization;
using System.Text;

namespace PhoneBox.Agent;

public sealed record CalendarEvent(string Title, long Start, long End, bool AllDay, string? Location, string Calendar, string Color);

public sealed record CalendarSnapshot(IReadOnlyList<CalendarEvent> Events, long Updated, IReadOnlyList<string> Errors);

/// <summary>
/// Upcoming events from iCalendar (ICS) feeds — Google's "secret address in iCal format", Outlook's published
/// calendar link, iCloud public calendars, any CalDAV server's export. Fetched every 10 minutes; the URLs stay on
/// this computer (they are secrets), the phone only gets the events of the next two weeks.
/// Recurring events are expanded for the usual rules (daily / weekly / monthly / yearly, INTERVAL, COUNT, UNTIL,
/// BYDAY, EXDATE, moved occurrences via RECURRENCE-ID).
/// </summary>
public sealed class CalendarMonitor(ConfigStore config, ILogger<CalendarMonitor> log) : BackgroundService
{
    private const int DaysAhead = 14;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private volatile CalendarSnapshot _snap = new([], 0, []);
    private string _sourcesKey = "";

    public CalendarSnapshot Snapshot() => _snap;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox");
        var next = DateTime.MinValue;
        var wakes = Wake.Count;
        while (!ct.IsCancellationRequested)
        {
            var sources = config.Get().Calendars.Where(c => !string.IsNullOrWhiteSpace(c.Url)).ToList();
            var key = string.Join("|", sources.Select(s => s.Url + s.Name + s.Color));
            // every 10 minutes, right after the calendars change, and on /api/refresh
            if (key != _sourcesKey || DateTime.UtcNow >= next || Wake.Count != wakes)
            {
                _sourcesKey = key;
                wakes = Wake.Count;
                await Refresh(sources, ct);
                next = DateTime.UtcNow.AddMinutes(10);
            }
            await Wake.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private async Task Refresh(List<CalendarSource> sources, CancellationToken ct)
    {
        var events = new List<CalendarEvent>();
        var errors = new List<string>();
        var from = DateTime.UtcNow.Date.AddDays(-1);
        var to = from.AddDays(DaysAhead + 1);
        foreach (var src in sources)
        {
            var name = string.IsNullOrWhiteSpace(src.Name) ? "Calendar" : src.Name;
            try
            {
                var url = src.Url.Trim();
                if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
                var text = await Http.GetStringAsync(url, ct);
                events.AddRange(Ics.Parse(text, from, to).Select(e => e with { Calendar = name, Color = src.Color ?? "#60a5fa" }));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add($"{name}: {ex.Message}");
                // keep the last good events of this calendar
                events.AddRange(_snap.Events.Where(e => e.Calendar == name));
            }
        }
        _snap = new(events.OrderBy(e => e.Start).ThenBy(e => e.Title).ToList(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), errors);
        if (sources.Count > 0) log.LogInformation("calendars: {Count} events{Errors}", events.Count, errors.Count > 0 ? "; " + string.Join("; ", errors) : "");
    }

    /// <summary>The event happening now (not all-day), if any, optionally only those whose title contains one of the names.</summary>
    public CalendarEvent? Current(string[] names)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _snap.Events.FirstOrDefault(e => !e.AllDay && e.Start <= now && e.End > now
            && (names.Length == 0 || names.Any(n => e.Title.Contains(n, StringComparison.OrdinalIgnoreCase))));
    }
}

/// <summary>A small iCalendar reader: VEVENTs with time zones and the common recurrence rules.</summary>
public static class Ics
{
    private sealed class Prop(string name, Dictionary<string, string> pars, string value)
    {
        public string Name = name; public Dictionary<string, string> Params = pars; public string Value = value;
    }

    public static IEnumerable<CalendarEvent> Parse(string text, DateTime fromUtc, DateTime toUtc)
    {
        var events = new List<List<Prop>>();
        List<Prop>? cur = null;
        foreach (var line in Unfold(text))
        {
            if (line == "BEGIN:VEVENT") { cur = []; continue; }
            if (line == "END:VEVENT") { if (cur is not null) events.Add(cur); cur = null; continue; }
            if (cur is null) continue;
            var p = ParseLine(line);
            if (p is not null) cur.Add(p);
        }

        // moved or cancelled occurrences of recurring events: UID + original start
        var overrides = new HashSet<(string, long)>();
        foreach (var e in events)
        {
            var rid = e.FirstOrDefault(p => p.Name == "RECURRENCE-ID");
            var uid = e.FirstOrDefault(p => p.Name == "UID")?.Value;
            if (rid is not null && uid is not null && ToUtc(rid, out _) is { } r) overrides.Add((uid, r.Ticks));
        }

        var result = new List<CalendarEvent>();
        foreach (var e in events)
        {
            if (e.FirstOrDefault(p => p.Name == "STATUS")?.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true) continue;
            var dtStart = e.FirstOrDefault(p => p.Name == "DTSTART");
            if (dtStart is null || ToUtc(dtStart, out var allDay) is not { } start) continue;
            var dtEnd = e.FirstOrDefault(p => p.Name == "DTEND");
            var end = dtEnd is not null && ToUtc(dtEnd, out _) is { } en ? en
                : e.FirstOrDefault(p => p.Name == "DURATION") is { } dur && TryDuration(dur.Value, out var d) ? start + d
                : allDay ? start.AddDays(1) : start;
            var length = end - start;
            var title = Unescape(e.FirstOrDefault(p => p.Name == "SUMMARY")?.Value ?? "(no title)");
            var location = e.FirstOrDefault(p => p.Name == "LOCATION")?.Value is { Length: > 0 } loc ? Unescape(loc) : null;
            var uid = e.FirstOrDefault(p => p.Name == "UID")?.Value ?? "";
            var rrule = e.FirstOrDefault(p => p.Name == "RRULE")?.Value;
            var isOverride = e.Any(p => p.Name == "RECURRENCE-ID");

            IEnumerable<DateTime> starts = [start];
            if (rrule is not null && !isOverride)
            {
                var exdates = new HashSet<long>(e.Where(p => p.Name == "EXDATE")
                    .SelectMany(p => p.Value.Split(',').Select(v => ToUtc(new Prop("EXDATE", p.Params, v), out _)))
                    .Where(x => x is not null).Select(x => x!.Value.Ticks));
                var zone = dtStart.Params.TryGetValue("TZID", out var tz) ? FindZone(tz) : null;
                starts = Expand(start, rrule, zone, allDay, fromUtc, toUtc)
                    .Where(s => !exdates.Contains(s.Ticks) && !overrides.Contains((uid, s.Ticks)));
            }
            foreach (var s in starts)
            {
                var f = s + length;
                if (f <= fromUtc && !(length == TimeSpan.Zero && s >= fromUtc)) continue;
                if (s >= toUtc) break;
                result.Add(new CalendarEvent(title, Ms(s), Ms(f), allDay, location, "", ""));
            }
        }
        return result;
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>Occurrence starts (UTC) of a recurring event; wall-clock times stay put across DST changes.</summary>
    private static IEnumerable<DateTime> Expand(DateTime firstUtc, string rrule, TimeZoneInfo? zone, bool allDay, DateTime fromUtc, DateTime toUtc)
    {
        var r = rrule.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0].ToUpperInvariant(), x => x[1]);
        var freq = r.GetValueOrDefault("FREQ", "");
        var interval = int.TryParse(r.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        var count = int.TryParse(r.GetValueOrDefault("COUNT"), out var c) ? c : int.MaxValue;
        DateTime? until = r.TryGetValue("UNTIL", out var u) && ToUtc(new Prop("UNTIL", [], u), out _) is { } ut ? ut : null;
        var byDay = r.TryGetValue("BYDAY", out var bd) ? bd.Split(',').Select(ParseDay).Where(x => x is not null).Select(x => x!.Value).ToList() : [];
        var byMonthDay = r.TryGetValue("BYMONTHDAY", out var bmd) ? bmd.Split(',').Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n != 0).ToList() : [];

        // work in the event's local wall-clock time
        var local = allDay || zone is null ? firstUtc : TimeZoneInfo.ConvertTimeFromUtc(firstUtc, zone);
        DateTime ToUtcTime(DateTime l) => allDay || zone is null ? l : SafeToUtc(l, zone);

        // without COUNT, skip the periods long before the window (a daily stand-up that started years ago)
        var skip = 0;
        if (count == int.MaxValue && fromUtc > firstUtc)
        {
            var days = (fromUtc - firstUtc).TotalDays;
            var periodDays = freq switch { "DAILY" => 1.0, "WEEKLY" => 7, "MONTHLY" => 31, "YEARLY" => 366, _ => double.MaxValue };
            skip = Math.Max(0, (int)(days / (periodDays * interval)) - 2);
        }
        var emitted = 0;
        for (var period = skip; period < skip + 3000 && emitted < count; period++)
        {
            IEnumerable<DateTime> candidates = freq switch
            {
                "DAILY" => [local.AddDays(period * interval)],
                "WEEKLY" => WeekDays(local, period * interval, byDay),
                "MONTHLY" => MonthDays(local, period * interval, byDay, byMonthDay),
                "YEARLY" => [local.AddYears(period * interval)],
                _ => period == 0 ? [local] : [],
            };
            var any = false;
            foreach (var l in candidates.OrderBy(x => x))
            {
                any = true;
                if (l < local) continue;
                var utc = ToUtcTime(l);
                if (until is not null && utc > until) yield break;
                if (utc >= toUtc) yield break;
                if (++emitted > count) yield break;
                yield return utc;
            }
            if (!any && freq is not ("WEEKLY" or "MONTHLY")) yield break;
        }
    }

    private static IEnumerable<DateTime> WeekDays(DateTime first, int weeks, List<(int Nth, DayOfWeek Day)> byDay)
    {
        var weekStart = first.Date.AddDays(-(((int)first.DayOfWeek + 6) % 7)).AddDays(weeks * 7); // Monday
        if (byDay.Count == 0) { yield return first.AddDays(weeks * 7); yield break; }
        foreach (var (_, day) in byDay)
            yield return weekStart.AddDays(((int)day + 6) % 7) + first.TimeOfDay;
    }

    private static IEnumerable<DateTime> MonthDays(DateTime first, int months, List<(int Nth, DayOfWeek Day)> byDay, List<int> byMonthDay)
    {
        var m = new DateTime(first.Year, first.Month, 1).AddMonths(months);
        var days = DateTime.DaysInMonth(m.Year, m.Month);
        if (byDay.Count > 0)
        {
            foreach (var (nth, day) in byDay)
            {
                var all = Enumerable.Range(1, days).Select(d => m.AddDays(d - 1)).Where(d => d.DayOfWeek == day).ToList();
                if (nth == 0) foreach (var d in all) yield return d + first.TimeOfDay;
                else if (nth > 0 && nth <= all.Count) yield return all[nth - 1] + first.TimeOfDay;
                else if (nth < 0 && -nth <= all.Count) yield return all[all.Count + nth] + first.TimeOfDay;
            }
            yield break;
        }
        foreach (var md in byMonthDay.Count > 0 ? byMonthDay : [first.Day])
        {
            var d = md > 0 ? md : days + md + 1;
            if (d >= 1 && d <= days) yield return m.AddDays(d - 1) + first.TimeOfDay;
        }
    }

    private static (int, DayOfWeek)? ParseDay(string s)
    {
        s = s.Trim();
        if (s.Length < 2) return null;
        var code = s[^2..];
        var nth = s.Length > 2 && int.TryParse(s[..^2], out var n) ? n : 0;
        DayOfWeek? day = code switch
        {
            "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
        };
        return day is null ? null : (nth, day.Value);
    }

    /// <summary>DTSTART-style value to UTC. Dates (all-day) stay at midnight, without a zone.</summary>
    private static DateTime? ToUtc(Prop p, out bool allDay)
    {
        var v = p.Value.Trim();
        allDay = v.Length == 8 || p.Params.GetValueOrDefault("VALUE") == "DATE";
        if (allDay)
            return DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
        var utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
        if (utc) return DateTime.SpecifyKind(t, DateTimeKind.Utc);
        var zone = p.Params.TryGetValue("TZID", out var tz) ? FindZone(tz) : null;
        return zone is null ? DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime() : SafeToUtc(t, zone);
    }

    private static DateTime SafeToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1); // skipped by a DST change
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static readonly Dictionary<string, TimeZoneInfo?> Zones = [];

    private static TimeZoneInfo? FindZone(string id)
    {
        id = id.Trim('"');
        lock (Zones)
        {
            if (Zones.TryGetValue(id, out var z)) return z;
            try { z = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch
            {
                // "/mozilla.org/20050126_1/Europe/Tallinn" and similar prefixes
                var tail = string.Join('/', id.Split('/').TakeLast(2));
                try { z = TimeZoneInfo.FindSystemTimeZoneById(tail); } catch { z = null; }
            }
            return Zones[id] = z;
        }
    }

    private static bool TryDuration(string v, out TimeSpan d)
    {
        // P1DT2H30M, PT45M, P1W
        d = TimeSpan.Zero;
        var m = System.Text.RegularExpressions.Regex.Match(v, @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return false;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        d = new TimeSpan(G(2) * 7 + G(3), G(4), G(5), G(6));
        if (m.Groups[1].Value == "-") d = -d;
        return true;
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) { sb.Append(raw, 1, raw.Length - 1); continue; }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear().Append(raw);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static Prop? ParseLine(string line)
    {
        // NAME;PARAM=VALUE;PARAM="VA:LUE":value — the first colon outside quotes ends the name part
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return null;
        var head = line[..colon].Split(';');
        var pars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in head.Skip(1))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) pars[kv[0].ToUpperInvariant()] = kv[1].Trim('"');
        }
        return new Prop(head[0].ToUpperInvariant(), pars, line[(colon + 1)..]);
    }

    private static string Unescape(string s) => s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
}
