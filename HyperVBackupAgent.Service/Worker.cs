namespace HyperVBackupAgent.Service;

using HyperVBackupAgent.Core;
using Microsoft.Extensions.Configuration;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHyperVService _hyperV;
    private readonly IBackupEngine _backupEngine;
    private readonly IRetentionService _retentionService;
    private DateTimeOffset? _lastIncrementalRun;
    private DateTimeOffset? _lastFullRun;

    public Worker(
        ILogger<Worker> logger,
        IConfiguration configuration,
        IHyperVService hyperV,
        IBackupEngine backupEngine,
        IRetentionService retentionService)
    {
        _logger = logger;
        _configuration = configuration;
        _hyperV = hyperV;
        _backupEngine = backupEngine;
        _retentionService = retentionService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = LoadOptions();
            if (!options.Enabled)
            {
                _logger.LogInformation("HyperVBackupAgent scheduler is disabled at {Time}", DateTimeOffset.Now);
                await Task.Delay(options.PollInterval, stoppingToken);
                continue;
            }

            await RunDueWorkAsync(options, DateTimeOffset.Now, stoppingToken);
            await Task.Delay(options.PollInterval, stoppingToken);
        }
    }

    private async Task RunDueWorkAsync(SchedulerOptions options, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ranBackup = false;
        if (IsWeeklyFullDue(options, now))
        {
            await RunBackupsAsync(options, full: true, cancellationToken);
            _lastFullRun = now;
            ranBackup = true;
        }

        if (IsDailyIncrementalDue(options, now))
        {
            await RunBackupsAsync(options, full: false, cancellationToken);
            _lastIncrementalRun = now;
            ranBackup = true;
        }

        if (ranBackup && options.ApplyRetentionAfterBackup)
        {
            var results = await _retentionService.ApplyRetentionAsync(
                new RetentionRequest(options.BackupRoot, options.KeepLastChains, options.KeepDays),
                cancellationToken);
            _logger.LogInformation(
                "Retention completed: Deleted={Deleted} Kept={Kept}",
                results.Count(result => result.Deleted),
                results.Count(result => !result.Deleted));
        }
    }

    private async Task RunBackupsAsync(SchedulerOptions options, bool full, CancellationToken cancellationToken)
    {
        var vmNames = await ResolveVmNamesAsync(options, cancellationToken);
        foreach (var vmName in vmNames)
        {
            try
            {
                var request = new BackupRequest(vmName, options.BackupRoot);
                var result = full
                    ? await _backupEngine.RunFullBackupAsync(request, cancellationToken)
                    : await _backupEngine.RunIncrementalBackupAsync(request, cancellationToken);

                _logger.LogInformation(
                    "{BackupType} backup finished for {VmName}: Status={Status} BackupId={BackupId} ChainId={ChainId} Error={Error}",
                    full ? "Full" : "Incremental",
                    vmName,
                    result.Status,
                    result.BackupId,
                    result.ChainId,
                    result.Error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{BackupType} backup failed for {VmName}", full ? "Full" : "Incremental", vmName);
            }
        }
    }

    private async Task<IReadOnlyList<string>> ResolveVmNamesAsync(SchedulerOptions options, CancellationToken cancellationToken)
    {
        if (options.VmNames.Length > 0)
        {
            return options.VmNames;
        }

        var vms = await _hyperV.ListVmsAsync(cancellationToken);
        return vms.Select(vm => vm.Name).ToArray();
    }

    private bool IsDailyIncrementalDue(SchedulerOptions options, DateTimeOffset now)
    {
        var scheduled = now.Date + options.DailyIncrementalTime;
        return now >= scheduled && (_lastIncrementalRun is null || _lastIncrementalRun.Value.Date < now.Date);
    }

    private bool IsWeeklyFullDue(SchedulerOptions options, DateTimeOffset now)
    {
        if (now.DayOfWeek != options.WeeklyFullDay)
        {
            return false;
        }

        var scheduled = now.Date + options.WeeklyFullTime;
        return now >= scheduled && (_lastFullRun is null || _lastFullRun.Value.Date < now.Date);
    }

    private SchedulerOptions LoadOptions()
    {
        var section = _configuration.GetSection("HyperVBackupAgent:Scheduler");
        return new SchedulerOptions
        {
            Enabled = section.GetValue("Enabled", true),
            BackupRoot = section["BackupRoot"] ?? _configuration["HyperVBackupAgent:BackupRoot"] ?? "backups",
            VmNames = section.GetSection("VmNames").Get<string[]>() ?? [],
            PollInterval = ParseTimeSpan(section["PollInterval"], TimeSpan.FromMinutes(1)),
            DailyIncrementalTime = ParseTimeSpan(section["DailyIncrementalTime"], TimeSpan.FromHours(22)),
            WeeklyFullDay = Enum.TryParse<DayOfWeek>(section["WeeklyFullDay"], ignoreCase: true, out var day) ? day : DayOfWeek.Sunday,
            WeeklyFullTime = ParseTimeSpan(section["WeeklyFullTime"], TimeSpan.FromHours(1)),
            ApplyRetentionAfterBackup = section.GetValue("ApplyRetentionAfterBackup", true),
            KeepLastChains = section.GetValue("KeepLastChains", 7),
            KeepDays = section.GetValue<int?>("KeepDays") ?? 30
        };
    }

    private static TimeSpan ParseTimeSpan(string? value, TimeSpan fallback)
        => TimeSpan.TryParse(value, out var parsed) ? parsed : fallback;
}
