using System.Collections.Concurrent;
using HyperVaultManager.Models;

namespace HyperVaultManager.Services;

/// <summary>Item enqueued for background execution against an agent.</summary>
public abstract record JobRequest(int RunId);

public sealed record BackupJobRequest(int BackupRunId) : JobRequest(BackupRunId);
public sealed record VerifyJobRequest(int VerificationRunId) : JobRequest(VerificationRunId);
public sealed record RestoreJobRequest(int RestoreRunId) : JobRequest(RestoreRunId);

/// <summary>In-memory queue consumed by <see cref="JobRunnerWorker"/>.</summary>
public interface IJobQueue
{
    void Enqueue(JobRequest request);
    int Pending { get; }
}

/// <summary>
/// Backed by a <see cref="BlockingCollection{T}"/>. <see cref="Take"/> blocks the
/// CALLING thread until an item is available, so callers must run it off the
/// startup thread (e.g. <c>await Task.Run(() => q.Take(ct), ct)</c>).
/// </summary>
public class JobQueue : IJobQueue
{
    private readonly BlockingCollection<JobRequest> _items = new(new ConcurrentQueue<JobRequest>());

    public void Enqueue(JobRequest request) => _items.Add(request);
    public JobRequest Take(CancellationToken ct) => _items.Take(ct);
    public int Pending => _items.Count;
}
