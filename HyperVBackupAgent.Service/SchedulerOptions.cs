namespace HyperVBackupAgent.Service;

public sealed record SchedulerOptions
{
    public bool Enabled { get; init; }
    public string BackupRoot { get; init; } = "backups";
    public string[] VmNames { get; init; } = [];
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan DailyIncrementalTime { get; init; } = TimeSpan.FromHours(22);
    public DayOfWeek WeeklyFullDay { get; init; } = DayOfWeek.Sunday;
    public TimeSpan WeeklyFullTime { get; init; } = TimeSpan.FromHours(1);
    public bool ApplyRetentionAfterBackup { get; init; } = true;
    public int KeepLastChains { get; init; } = 7;
    public int? KeepDays { get; init; } = 30;
}
