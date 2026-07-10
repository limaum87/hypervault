using HyperVaultManager.Data;
using HyperVaultManager.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HyperVaultManager.Services;

/// <summary>Periodically fires enabled backup jobs whose next-run time has arrived.</summary>
public class SchedulerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IJobQueue _queue;
    private readonly ILogger<SchedulerWorker> _logger;
    private readonly TimeSpan _tick = TimeSpan.FromSeconds(30);

    public SchedulerWorker(IServiceScopeFactory scopes, IJobQueue queue, ILogger<SchedulerWorker> logger)
    {
        _scopes = scopes; _queue = queue; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _logger.LogInformation("SchedulerWorker started");
        while (!stop.IsCancellationRequested)
        {
            try { await TickAsync(stop); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Scheduler tick failed"); }
            try { await Task.Delay(_tick, stop); }
            catch (OperationCanceledException) { throw; }
        }
        _logger.LogInformation("SchedulerWorker stopped");
    }

    private async Task TickAsync(CancellationToken stop)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.Jobs.Where(j => j.Enabled && j.NextRunAt != null).ToListAsync(stop);
        var due = candidates.Where(j => j.NextRunAt!.Value <= now).ToList();

        foreach (var job in due)
        {
            try
            {
                _logger.LogInformation("Firing scheduled job {Id} '{Name}'", job.Id, job.Name);
                await SchedulerFire.FireJobAsync(db, _queue, job, stop);
            }
            catch (Exception ex) { _logger.LogError(ex, "Failed to fire job {Id}", job.Id); }
        }

        // Recompute NextRunAt for jobs that are missing one but enabled.
        var enabled = await db.Jobs.Where(j => j.Enabled).ToListAsync(stop);
        foreach (var job in enabled)
        {
            // Manual jobs never auto-fire.
            if (string.IsNullOrWhiteSpace(job.CronSchedule) ||
                string.Equals(job.ScheduleType, ScheduleTypes.Manual, StringComparison.OrdinalIgnoreCase))
            {
                if (job.NextRunAt != null) job.NextRunAt = null;
                continue;
            }
            var tz = ScheduleBuilder.ResolveTimeZone(job.TimeZone);
            var next = CronNextRun.Next(job.CronSchedule, job.LastRunAt ?? now, tz);
            if (next != job.NextRunAt)
            {
                job.NextRunAt = next;
            }
        }
        await db.SaveChangesAsync(stop);
    }
}

/// <summary>Shared logic to create a BackupRun from a job and enqueue it.</summary>
public static class SchedulerFire
{
    public static async Task<BackupRun> FireJobAsync(ManagerDbContext db, IJobQueue queue, BackupJob job, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new BackupRun
        {
            JobId = job.Id,
            HostId = job.HostId,
            VmId = job.VmId,
            StorageId = job.StorageId,
            Type = job.Type,
            Status = RunStatuses.Queued,
            QueuedAt = now
        };
        db.BackupRuns.Add(run);
        job.LastRunAt = now;
        var tz = ScheduleBuilder.ResolveTimeZone(job.TimeZone);
        job.NextRunAt = CronNextRun.Next(job.CronSchedule, now, tz);
        await db.SaveChangesAsync(ct);
        queue.Enqueue(new BackupJobRequest(run.Id));
        return run;
    }
}
