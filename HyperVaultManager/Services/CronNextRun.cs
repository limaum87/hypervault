using System.Globalization;

namespace HyperVaultManager.Services;

/// <summary>
/// Minimal 5-field cron (minute hour day-of-month month day-of-week) "next run"
/// calculator supporting '*', 'a', 'a-b', 'a,b,c' and '*/n'.
///
/// The cron is evaluated in the job's own <see cref="TimeZoneInfo"/> (defaults
/// to UTC): a user typing "10:50" in America/Sao_Paulo gets exactly 10:50 there.
///
/// The search runs in the job's LOCAL time (not UTC) so that timezone offsets
/// don't make the iteration "land" on the wrong local day, and converts the
/// matched local instant to UTC only at the end. When a coarser field fails to
/// match, finer fields are reset to 0 (classic cron-next behavior).
/// </summary>
public static class CronNextRun
{
    public static DateTimeOffset? Next(string? cron, DateTimeOffset after, TimeZoneInfo? tz = null)
    {
        if (string.IsNullOrWhiteSpace(cron)) return null;
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return null;

        int[][] fields;
        try { fields = parts.Select(ParseField).ToArray(); }
        catch { return null; }

        tz ??= TimeZoneInfo.Utc;

        // Iterate candidate instants expressed in the JOB's local timezone.
        var localAfter = TimeZoneInfo.ConvertTime(after, tz);
        var c = new DateTime(localAfter.Year, localAfter.Month, localAfter.Day,
                             localAfter.Hour, localAfter.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var limit = localAfter.AddYears(1);

        while (c <= limit)
        {
            if (!fields[3].Contains(c.Month)) { c = FirstOfNextMonth(c); continue; }
            if (!fields[4].Contains((int)c.DayOfWeek) || !fields[2].Contains(c.Day)) { c = StartOfNextLocalDay(c); continue; }
            if (!fields[1].Contains(c.Hour)) { c = StartOfNextLocalHour(c); continue; }
            if (!fields[0].Contains(c.Minute)) { c = c.AddMinutes(1); continue; }

            // All fields match in local time -> convert this local instant to UTC.
            try
            {
                var utc = TimeZoneInfo.ConvertTimeToUtc(c, tz);
                return new DateTimeOffset(utc, TimeSpan.Zero);
            }
            catch (ArgumentException)
            {
                // Skipped/ambiguous local time during a DST transition: nudge forward.
                c = c.AddMinutes(30);
            }
        }
        return null;
    }

    private static DateTime StartOfNextLocalDay(DateTime cur)
    {
        var n = cur.AddDays(1);
        return new DateTime(n.Year, n.Month, n.Day, 0, 0, 0, DateTimeKind.Unspecified);
    }

    private static DateTime StartOfNextLocalHour(DateTime cur)
    {
        var n = cur.AddHours(1);
        return new DateTime(n.Year, n.Month, n.Day, n.Hour, 0, 0, DateTimeKind.Unspecified);
    }

    private static DateTime FirstOfNextMonth(DateTime cur)
    {
        var firstOfThis = new DateTime(cur.Year, cur.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return firstOfThis.AddMonths(1);
    }

    private static int[] ParseField(string field, int index)
    {
        var (min, max) = index switch
        {
            0 => (0, 59),
            1 => (0, 23),
            2 => (1, 31),
            3 => (1, 12),
            4 => (0, 6),
            _ => throw new FormatException("bad cron field index")
        };
        var set = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            if (part == "*") { AddRange(set, min, max, 1); continue; }
            if (part.StartsWith("*/"))
            {
                if (int.TryParse(part[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var step) && step > 0)
                    AddRange(set, min, max, step);
                else throw new FormatException("bad step");
                continue;
            }
            var dash = part.Split('-');
            if (dash.Length == 2 &&
                int.TryParse(dash[0], out var lo) && int.TryParse(dash[1], out var hi) && lo <= hi)
            {
                AddRange(set, Math.Max(lo, min), Math.Min(hi, max), 1);
                continue;
            }
            if (int.TryParse(part, out var v)) set.Add(Clamp(v, min, max));
            else throw new FormatException("bad value: " + part);
        }
        return set.ToArray();
    }

    private static void AddRange(HashSet<int> set, int lo, int hi, int step)
    {
        for (var v = lo; v <= hi; v += step) set.Add(v);
    }

    private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
}
