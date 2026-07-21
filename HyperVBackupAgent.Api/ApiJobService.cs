using System.Collections.Concurrent;
using System.Text.Json;
using Serilog.Context;

namespace HyperVBackupAgent.Api;

public sealed class ApiJobService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, ApiJobRecord> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storePath;
    private readonly ILogger<ApiJobService> _logger;
    private readonly object _saveLock = new();

    public ApiJobService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<ApiJobService> logger)
    {
        _logger = logger;
        _storePath = ResolveStorePath(configuration["HyperVBackupAgent:Api:Jobs:StorePath"], environment.ContentRootPath);
        LoadJobs();
    }

    public IReadOnlyList<ApiJobRecord> ListJobs()
        => _jobs.Values
            .OrderByDescending(job => job.CreatedAt)
            .ToArray();

    public ApiJobRecord? GetJob(string jobId)
        => _jobs.TryGetValue(jobId, out var job) ? job : null;

    public ApiJobRecord Enqueue(
        string type,
        string? vmNameOrId,
        string? targetPath,
        Func<CancellationToken, Task<ApiJobOutcome>> operation,
        string? correlationId = null)
    {
        var job = new ApiJobRecord(
            Guid.NewGuid().ToString("N"),
            type,
            ApiJobStatus.Queued,
            DateTimeOffset.UtcNow,
            null,
            null,
            vmNameOrId,
            targetPath,
            null,
            null,
            correlationId);

        _jobs[job.JobId] = job;
        SaveJobs();
        _logger.LogInformation(
            "API job {JobId} queued: {JobType} for {VmNameOrId}",
            job.JobId,
            job.Type,
            job.VmNameOrId);

        var cancellation = new CancellationTokenSource();
        _cancellations[job.JobId] = cancellation;
        _ = Task.Run(() => RunJobAsync(job.JobId, operation, cancellation), CancellationToken.None);
        return job;
    }

    public bool Cancel(string jobId)
    {
        if (!_cancellations.TryGetValue(jobId, out var cancellation))
        {
            return false;
        }

        _logger.LogInformation("Cancel requested for API job {JobId}", jobId);
        cancellation.Cancel();
        return true;
    }

    private async Task RunJobAsync(string jobId, Func<CancellationToken, Task<ApiJobOutcome>> operation, CancellationTokenSource cancellation)
    {
        var current = GetJob(jobId);
        using (LogContext.PushProperty("JobId", jobId))
        using (LogContext.PushProperty("JobType", current?.Type))
        using (LogContext.PushProperty("CorrelationId", current?.CorrelationId ?? jobId))
        {
            Update(jobId, job => job with { Status = ApiJobStatus.Running, StartedAt = DateTimeOffset.UtcNow });
            _logger.LogInformation("API job {JobId} started", jobId);
            try
            {
                var outcome = await operation(cancellation.Token);
                Update(jobId, job => job with
                {
                    Status = ApiJobStatus.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ResultPath = outcome.ResultPath,
                    Message = outcome.Message,
                    SizeBytes = outcome.SizeBytes
                });
                _logger.LogInformation("API job {JobId} completed", jobId);
            }
            catch (OperationCanceledException)
            {
                Update(jobId, job => job with
                {
                    Status = ApiJobStatus.Canceled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Message = "Job was canceled."
                });
                _logger.LogInformation("API job {JobId} canceled", jobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API job {JobId} failed", jobId);
                Update(jobId, job => job with
                {
                    Status = ApiJobStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = ex.Message
                });
            }
            finally
            {
                _cancellations.TryRemove(jobId, out _);
                cancellation.Dispose();
            }
        }
    }

    private void Update(string jobId, Func<ApiJobRecord, ApiJobRecord> update)
    {
        _jobs.AddOrUpdate(jobId, _ => throw new InvalidOperationException($"Job not found: {jobId}"), (_, current) => update(current));
        SaveJobs();
    }

    private void LoadJobs()
    {
        if (!File.Exists(_storePath))
        {
            return;
        }

        try
        {
            var jobs = JsonSerializer.Deserialize<List<ApiJobRecord>>(File.ReadAllText(_storePath), JsonOptions) ?? [];
            foreach (var job in jobs)
            {
                var normalized = job.Status is ApiJobStatus.Running or ApiJobStatus.Queued
                    ? job with
                    {
                        Status = ApiJobStatus.Failed,
                        CompletedAt = DateTimeOffset.UtcNow,
                        Error = "Agent restarted before this job completed."
                    }
                    : job;
                _jobs[normalized.JobId] = normalized;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load API job history from {StorePath}", _storePath);
        }
    }

    private void SaveJobs()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var jobs = _jobs.Values.OrderByDescending(job => job.CreatedAt).Take(500).ToArray();
            File.WriteAllText(_storePath, JsonSerializer.Serialize(jobs, JsonOptions));
        }
    }

    private static string ResolveStorePath(string? configuredPath, string contentRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var baseDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "HyperVBackupAgent",
                "jobs")
            : Path.Combine(contentRootPath, "jobs");

        return Path.Combine(baseDirectory, "api-jobs.json");
    }
}

public enum ApiJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed record ApiJobRecord(
    string JobId,
    string Type,
    ApiJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? VmNameOrId,
    string? TargetPath,
    string? ResultPath,
    string? Error,
    string? CorrelationId = null,
    string? Message = null,
    long SizeBytes = 0);

public sealed record ApiJobOutcome(string? ResultPath = null, string? Message = null, long SizeBytes = 0);
