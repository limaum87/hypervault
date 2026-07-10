using System.Globalization;

namespace HyperVaultManager.Services;

/// <summary>
/// Minimal 5-field cron (minute hour day-of-month month day-of-week) "next run"
/// calculator supporting '*', 'a', 'a-b', 'a,b,c' and '*/n'. Good enough for
/// the MVP scheduling needs (daily/weekly jobs).
/// </summary>
public static class CronNextRun
{
    public static DateTimeOffset? Next(string? cron, DateTimeOffset after)
    {
        if (string.IsNullOrWhiteSpace(cron)) return null;
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return null;

        int[][] fields;
        try { fields = parts.Select(ParseField).ToArray(); }
        catch { return null; }

        var from = after.UtcDateTime.AddSeconds(1).TrimToMinute();
        var cur = from;
        // cap the search to ~1 year to avoid infinite loops on impossible exprs
        var limit = from.AddYears(1);
        while (cur <= limit)
        {
            if (!fields[3].Contains(cur.Month)) { cur = cur.AddMonthsToFirstAllowed(fields[3]); continue; }
            if (!fields[2].Contains(cur.Day)) { cur = cur.AddDaysToFirstAllowed(fields[2], cur.Month, cur.Year); continue; }
            if (!fields[4].Contains((int)cur.DayOfWeek)) { cur = cur.AddDays(1).TrimToMinute(); continue; }
            if (!fields[1].Contains(cur.Hour)) { cur = cur.AddHours(1).TrimToMinute(); continue; }
            if (!fields[0].Contains(cur.Minute)) { cur = cur.AddMinutes(1); continue; }
            return new DateTimeOffset(cur, TimeSpan.Zero);
        }
        return null;
    }

    private static DateTime TrimToMinute(this DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, DateTimeKind.Utc);

    private static DateTime AddMonthsToFirstAllowed(this DateTime cur, int[] allowedMonths)
    {
        var next = cur;
        for (var i = 0; i < 24; i++)
        {
            next = new DateTime(next.Year, next.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            if (allowedMonths.Contains(next.Month)) return next;
        }
        return cur.AddYears(1);
    }

    private static DateTime AddDaysToFirstAllowed(this DateTime cur, int[] allowedDays, int month, int year)
    {
        var next = cur.AddDays(1);
        var daysInMonth = DateTime.DaysInMonth(next.Year, next.Month);
        for (var i = 0; i < 366; i++)
        {
            if (allowedDays.Contains(next.Day)) return new DateTime(next.Year, next.Month, next.Day, 0, 0, 0, DateTimeKind.Utc);
            next = next.AddDays(1);
        }
        return cur.AddMonths(1);
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
