using System.Text.Json.Nodes;
using HyperVaultManager.Data;
using HyperVaultManager.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HyperVaultManager.Services;

/// <summary>Consumes the job queue and drives each operation through the agent API.</summary>
public class JobRunnerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IJobQueue _queue;
    private readonly AgentClient _agent;
    private readonly SecretProtector _secrets;
    private readonly ILogger<JobRunnerWorker> _logger;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _backupTimeout;
    private readonly TimeSpan _verifyTimeout;
    private readonly TimeSpan _restoreTimeout;

    public JobRunnerWorker(IServiceScopeFactory scopes, IJobQueue queue, AgentClient agent, SecretProtector secrets, ILogger<JobRunnerWorker> logger,
        IConfiguration cfg)
    {
        _scopes = scopes;
        _queue = queue;
        _agent = agent;
        _secrets = secrets;
        _logger = logger;
        _poll = TimeSpan.FromSeconds(cfg.GetValue("Manager:JobPollIntervalSeconds", 5));
        _backupTimeout = TimeSpan.FromHours(cfg.GetValue("Manager:BackupTimeoutHours", 8));
        _verifyTimeout = TimeSpan.FromHours(cfg.GetValue("Manager:VerifyTimeoutHours", 4));
        _restoreTimeout = TimeSpan.FromHours(cfg.GetValue("Manager:RestoreTimeoutHours", 12));
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var q = (JobQueue)_queue;
        _logger.LogInformation("JobRunnerWorker started");
        while (!stop.IsCancellationRequested)
        {
            JobRequest request;
            try
            {
                // Run the blocking Take on the threadpool so this async method
                // yields back to the host at the first await — letting the web
                // server start instead of blocking host startup.
                request = await Task.Run(() => q.Take(stop), stop);
            }
            catch (OperationCanceledException) { break; }

            try { await HandleAsync(request, stop); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Unhandled error processing {Request}", request); }
        }
        _logger.LogInformation("JobRunnerWorker stopped");
    }

    private async Task HandleAsync(JobRequest request, CancellationToken stop)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ManagerDbContext>();
        switch (request)
        {
            case BackupJobRequest b: await RunBackupAsync(db, b.BackupRunId, stop); break;
            case VerifyJobRequest v: await RunVerifyAsync(db, v.VerificationRunId, stop); break;
            case RestoreJobRequest r: await RunRestoreAsync(db, r.RestoreRunId, stop); break;
        }
    }

    /// <summary>Builds SMB credentials for a storage target, decrypting the password.
    /// Returns null for non-SMB storages or when no username/password is set.</summary>
    private SmbCredentials? StorageCreds(StorageTarget s)
    {
        if (s.Type != StorageTypes.Smb) return null;
        return SmbCredentials.From(s.SmbUsername, _secrets.Unprotect(s.SmbPasswordCipher), s.SmbDomain);
    }

    private async Task RunBackupAsync(ManagerDbContext db, int runId, CancellationToken stop)
    {
        var run = await db.BackupRuns.Include(x => x.Host).Include(x => x.Vm).Include(x => x.Storage)
            .FirstOrDefaultAsync(x => x.Id == runId, stop);
        if (run is null || run.Host is null || run.Vm is null || run.Storage is null)
        {
            _logger.LogWarning("Backup run {Id} missing dependencies", runId); return;
        }

        var destination = run.Storage.Path;
        // Decrypt SMB credentials (if any) so the agent can mount the share.
        var smb = StorageCreds(run.Storage);
        await MarkRunning(db, run, stop);

        try
        {
            // 1) Preflight (best effort, per WEB_AGENT_HANDOFF)
            try
            {
                var pre = await _agent.PreflightBackupAsync(run.Host, run.Vm.ExternalId, destination, smb, stop);
                if (pre is not null && TryGetBool(pre, "canProceed") is false)
                    throw new InvalidOperationException($"Preflight blocked: {pre.ToJsonString()}");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Preflight failed for run {Id} (continuing)", runId); }

            // 2) Enqueue agent job
            var agentJob = await _agent.EnqueueBackupAsync(run.Host, run.Type, run.Vm.ExternalId, destination, smb, stop);
            run.AgentJobId = agentJob.JobId;
            await Save(db, stop);

            // 3) Poll
            var final = await PollAsync(db, run, _backupTimeout, stop);
            ApplyJobResult(run, final);

            // 4) Retention (best effort): after a successful backup, prune old
            // chains so only the newest full chain (+ its incrementals) remains.
            // Never fails the run — a retention problem must not turn a good
            // backup red; it is logged and retried on the next successful run.
            if (run.Status == RunStatuses.Succeeded)
                await ApplyRetentionBestEffortAsync(db, run, stop);
        }
        catch (Exception ex)
        {
            Fail(run, ex);
        }
        finally { await Finish(db, run, stop); }
    }

    /// <summary>Applies the job's chain-retention policy through the agent: keeps the
    /// newest <see cref="BackupJob.KeepChains"/> chain(s) (default 1 = newest full only)
    /// and prunes chains older than <see cref="BackupJob.RetentionDays"/> when set.
    /// SMB-backed storages are skipped for now: the agent's retention endpoint has
    /// no per-call credentials, so it can only prune roots it can reach locally.</summary>
    private async Task ApplyRetentionBestEffortAsync(ManagerDbContext db, BackupRun run, CancellationToken stop)
    {
        if (run.Host is null || run.Storage is null) return;
        if (run.Storage.Type == StorageTypes.Smb)
        {
            _logger.LogWarning("Retention skipped for run {Id}: SMB storage '{Storage}' is not supported yet", run.Id, run.Storage.Name);
            return;
        }

        try
        {
            // Job settings drive retention; manual runs (no job) fall back to keep-1.
            var job = run.JobId is int jobId
                ? await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, stop)
                : null;
            var keepChains = Math.Max(1, job?.KeepChains ?? 1);
            int? keepDays = job is { RetentionDays: > 0 } ? job.RetentionDays : null;

            var results = await _agent.ApplyRetentionAsync(run.Host, run.Storage.Path, keepChains, keepDays, dryRun: false, ct: stop);
            var deleted = results?.Where(r => r?["deleted"]?.GetValue<bool>() == true).ToList() ?? [];
            _logger.LogInformation("Retention after run {Id} (keep {Keep} chains, {Days}d): deleted {Deleted}/{Total} chain(s)",
                run.Id, keepChains, keepDays?.ToString() ?? "-", deleted.Count, results?.Count ?? 0);
            if (deleted.Count > 0)
            {
                var names = string.Join(", ", deleted.Select(r => r?["chainId"]?.ToString()));
                run.Message = $"{run.Message} | retention: deleted {deleted.Count} old chain(s): {names}".TrimStart(' ', '|');
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention failed after run {Id} (backup itself succeeded)", run.Id);
        }
    }

    private async Task RunVerifyAsync(ManagerDbContext db, int runId, CancellationToken stop)
    {
        var run = await db.VerificationRuns.Include(x => x.Host).FirstOrDefaultAsync(x => x.Id == runId, stop);
        if (run is null || run.Host is null) { _logger.LogWarning("Verify run {Id} missing host", runId); return; }

        await MarkRunning(db, run, stop);
        try
        {
            var agentJob = run.Kind == VerifyKinds.Restore
                ? await _agent.EnqueueVerifyRestoreAsync(run.Host, run.TargetPath, stop)
                : await _agent.EnqueueVerifyChainAsync(run.Host, run.TargetPath, stop);
            run.AgentJobId = agentJob.JobId;
            await Save(db, stop);

            var final = await PollAsync(db, run, _verifyTimeout, stop);
            run.Status = AgentClient.NormalizeJobStatus(final);
            run.IsValid = final?["isValid"]?.GetValue<bool>();
            run.Errors = Join(final?["errors"]);
            run.Warnings = Join(final?["warnings"]);
        }
        catch (Exception ex) { Fail(run, ex); }
        finally { await Finish(db, run, stop); }
    }

    private async Task RunRestoreAsync(ManagerDbContext db, int runId, CancellationToken stop)
    {
        var run = await db.RestoreRuns.Include(x => x.TargetHost).FirstOrDefaultAsync(x => x.Id == runId, stop);
        if (run is null || run.TargetHost is null) { _logger.LogWarning("Restore run {Id} missing target host", runId); return; }

        await MarkRunning(db, run, stop);
        try
        {
            var createVm = run.Mode != RestoreModes.DiskOnly;
            var payload = new RestoreRequestPayload(run.RestorePointPath, run.Destination, run.NewName, run.OverwriteExisting, run.TargetBackupId, createVm);
            var agentJob = await _agent.EnqueueRestoreAsync(run.TargetHost, payload, stop);
            run.AgentJobId = agentJob.JobId;
            await Save(db, stop);

            var final = await PollAsync(db, run, _restoreTimeout, stop);
            run.Status = AgentClient.NormalizeJobStatus(final);
            run.Message = final?["message"]?.ToString();
            run.Error = final?["error"]?.ToString();
        }
        catch (Exception ex) { Fail(run, ex); }
        finally { await Finish(db, run, stop); }
    }

    // ---- polling ----
    private async Task<JsonObject?> PollAsync(ManagerDbContext db, BackupRun run, TimeSpan timeout, CancellationToken stop)
        => await PollJobAsync(db, run.Host!, run.AgentJobId!, timeout, stop);

    private async Task<JsonObject?> PollAsync(ManagerDbContext db, VerificationRun run, TimeSpan timeout, CancellationToken stop)
        => await PollJobAsync(db, run.Host!, run.AgentJobId!, timeout, stop);

    private async Task<JsonObject?> PollAsync(ManagerDbContext db, RestoreRun run, TimeSpan timeout, CancellationToken stop)
        => await PollJobAsync(db, run.TargetHost!, run.AgentJobId!, timeout, stop);

    private async Task<JsonObject?> PollJobAsync(ManagerDbContext db, HyperVHost host, string agentJobId, TimeSpan timeout, CancellationToken stop)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        JsonObject? last = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                last = await _agent.GetJobAsync(host, agentJobId, stop);
                var status = AgentClient.NormalizeJobStatus(last);
                if (status == RunStatuses.Succeeded || status == RunStatuses.Failed || status == RunStatuses.Canceled)
                    return last;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Transient error polling agent job {Id}", agentJobId); }
            await Task.Delay(_poll, stop);
        }
        return last;
    }

    // ---- run state helpers ----
    private static async Task MarkRunning<T>(ManagerDbContext db, T run, CancellationToken ct) where T : class
    {
        var now = DateTimeOffset.UtcNow;
        switch (run)
        {
            case BackupRun b: b.Status = RunStatuses.Running; b.StartedAt = now; break;
            case VerificationRun v: v.Status = RunStatuses.Running; v.StartedAt = now; break;
            case RestoreRun r: r.Status = RunStatuses.Running; r.StartedAt = now; break;
        }
        await db.SaveChangesAsync(ct);
    }

    private static void ApplyJobResult(BackupRun run, JsonObject? final)
    {
        run.Status = AgentClient.NormalizeJobStatus(final);
        run.CorrelationId = final?["correlationId"]?.ToString();
        run.ResultPath = final?["resultPath"]?.ToString() ?? final?["targetPath"]?.ToString();
        run.Message = final?["message"]?.ToString();
        run.Error = final?["error"]?.ToString();
        run.BackupId = final?["backupId"]?.ToString();
        run.ChainId = final?["chainId"]?.ToString();
        run.SizeBytes = final?["sizeBytes"]?.GetValue<long>() ?? 0;
    }

    private static void Fail<T>(T run, Exception ex) where T : class
    {
        switch (run)
        {
            case BackupRun b: b.Status = RunStatuses.Failed; b.Error = ex.Message; break;
            case VerificationRun v: v.Status = RunStatuses.Failed; v.Errors = ex.Message; break;
            case RestoreRun r: r.Status = RunStatuses.Failed; r.Error = ex.Message; break;
        }
    }

    private static async Task Finish<T>(ManagerDbContext db, T run, CancellationToken ct) where T : class
    {
        var now = DateTimeOffset.UtcNow;
        switch (run)
        {
            case BackupRun b: b.CompletedAt = now; if (b.StartedAt.HasValue) b.DurationSeconds = (long)(now - b.StartedAt.Value).TotalSeconds; break;
            case VerificationRun v: v.CompletedAt = now; break;
            case RestoreRun r: r.CompletedAt = now; break;
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task Save(ManagerDbContext db, CancellationToken ct) => await db.SaveChangesAsync(ct);

    private static bool? TryGetBool(JsonObject o, string key)
    {
        if (o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    private static string? Join(JsonNode? node) => node is JsonArray arr ? string.Join("; ", arr.Select(x => x?.ToString())) : node?.ToString();
}
