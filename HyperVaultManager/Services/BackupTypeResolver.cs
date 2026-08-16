using HyperVaultManager.Data;
using HyperVaultManager.Models;
using Microsoft.EntityFrameworkCore;

namespace HyperVaultManager.Services;

/// <summary>Decides the effective backup type (full vs incremental) to run for a job
/// at fire time. Implements GFS-style rotation: an incremental job whose
/// <see cref="BackupJob.FullIntervalDays"/> is &gt; 0 normally runs incrementally but
/// periodically takes a FULL backup to refresh the chain. It also seeds the chain
/// with a full when no successful full exists yet. Full jobs always run full.</summary>
public static class BackupTypeResolver
{
    /// <summary>Returns the type (<see cref="JobTypes.Full"/> or <see cref="JobTypes.Incremental"/>)
    /// that should actually run for <paramref name="job"/> at <paramref name="now"/>.</summary>
    public static async Task<string> ResolveAsync(
        ManagerDbContext db, BackupJob job, DateTimeOffset now, CancellationToken ct = default)
    {
        // A full job always runs full; rotation only applies to incremental jobs.
        if (!string.Equals(job.Type, JobTypes.Incremental, StringComparison.OrdinalIgnoreCase))
            return JobTypes.Full;

        // Pure incremental: no rotation configured => legacy single-type behavior.
        if (job.FullIntervalDays <= 0)
            return JobTypes.Incremental;

        // Rotation enabled: take a full when there is no prior successful full yet
        // (seed the chain) or when FullIntervalDays have elapsed since the last one.
        var lastFull = await LastSuccessfulFullAtAsync(db, job.Id, ct);
        if (lastFull is null)
            return JobTypes.Full;

        return (now - lastFull.Value) >= TimeSpan.FromDays(job.FullIntervalDays)
            ? JobTypes.Full
            : JobTypes.Incremental;
    }

    /// <summary>Timestamp of the most recent SUCCEEDED full backup for the job, or null
    /// when the job has never produced a successful full. Uses CompletedAt (always set
    /// for succeeded runs by JobRunnerWorker.Finish).</summary>
    public static async Task<DateTimeOffset?> LastSuccessfulFullAtAsync(
        ManagerDbContext db, int jobId, CancellationToken ct = default)
    {
        var last = await db.BackupRuns
            .AsNoTracking()
            .Where(r => r.JobId == jobId
                && r.Type == JobTypes.Full
                && r.Status == RunStatuses.Succeeded)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);
        // Succeeded runs always have CompletedAt; fall back to QueuedAt just in case.
        return last?.CompletedAt ?? last?.QueuedAt;
    }
}
