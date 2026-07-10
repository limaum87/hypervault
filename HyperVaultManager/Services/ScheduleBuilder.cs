using System.Globalization;
using HyperVaultManager.Models;

namespace HyperVaultManager.Services;

/// <summary>Builds a 5-field cron expression from the friendly scheduling fields,
/// and resolves IANA timezone ids safely (falling back to UTC when unknown).</summary>
public static class ScheduleBuilder
{
    /// <summary>Returns a cron expression (always in the job's TZ) or "" for manual.</summary>
    public static string BuildCron(string scheduleType, string time, string weekdays, int? dayOfMonth)
    {
        var (hh, mm) = ParseTime(time);
        return scheduleType?.ToLowerInvariant() switch
        {
            ScheduleTypes.Daily => $"{mm} {hh} * * *",
            ScheduleTypes.Weekly => $"{mm} {hh} * * {NormalizeWeekdays(weekdays)}",
            ScheduleTypes.Monthly => $"{mm} {hh} {ClampDay(dayOfMonth)} * *",
            _ => CronPresets.Disabled // manual / unknown
        };
    }

    /// <summary>Resolves an IANA/Windows timezone id to a TimeZoneInfo, defaulting to UTC.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch { return TimeZoneInfo.Utc; }
    }

    /// <summary>Validates/normalizes an IANA timezone id; returns "" if unknown.</summary>
    public static bool IsValidTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); return true; }
        catch { return false; }
    }

    private static (int hh, int mm) ParseTime(string? time)
    {
        time = (time ?? "00:00").Trim();
        var ok = TimeSpan.TryParse(time, CultureInfo.InvariantCulture, out var ts);
        if (!ok || ts.TotalHours >= 24) return (0, 0);
        return (ts.Hours, ts.Minutes);
    }

    private static string NormalizeWeekdays(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return "*";
        var days = new HashSet<int>();
        foreach (var p in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(p.Trim(), out var d) && d is >= 0 and <= 6) days.Add(d);
        }
        return days.Count == 0 ? "*" : string.Join(",", days.Order());
    }

    private static int ClampDay(int? d) => (d is >= 1 and <= 31) ? d.Value : 1;
}
