using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class FileSystemRetentionService : IRetentionService
{
    private readonly IMetadataRepository _metadata;

    public FileSystemRetentionService(IMetadataRepository metadata)
    {
        _metadata = metadata;
    }

    public async Task<IReadOnlyList<RetentionResult>> ApplyRetentionAsync(RetentionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.KeepLastChains < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request.KeepLastChains), "At least one valid chain must be kept.");
        }

        var backupRoot = Path.GetFullPath(request.BackupRoot);
        if (!Directory.Exists(backupRoot))
        {
            return [];
        }

        var chains = new List<(string Path, BackupChainMetadata Metadata)>();
        foreach (var chainFile in Directory.EnumerateFiles(backupRoot, "chain.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chainDirectory = Path.GetDirectoryName(chainFile)!;
            try
            {
                chains.Add((chainDirectory, await _metadata.LoadChainAsync(chainDirectory, cancellationToken)));
            }
            catch
            {
                continue;
            }
        }

        var results = new List<RetentionResult>();
        foreach (var vmGroup in chains.GroupBy(item => item.Metadata.VmId))
        {
            var ordered = vmGroup
                .OrderByDescending(item => item.Metadata.CreatedAt)
                .ToArray();

            var newestValidFull = ordered.FirstOrDefault(item => IsValidFullChain(item.Metadata));
            var protectedPath = newestValidFull.Path;

            for (var index = 0; index < ordered.Length; index++)
            {
                var item = ordered[index];
                if (item.Path == protectedPath)
                {
                    results.Add(Result(item, deleted: false, "protected-last-valid-full"));
                    continue;
                }

                if (item.Metadata.Status != BackupStatus.Completed)
                {
                    results.Add(Result(item, deleted: false, "skipped-incomplete-chain", "Incomplete chains are not deleted automatically."));
                    continue;
                }

                var exceedsChainLimit = index >= request.KeepLastChains;
                var exceedsAgeLimit = request.KeepDays is not null &&
                    item.Metadata.CreatedAt < DateTimeOffset.UtcNow.AddDays(-request.KeepDays.Value);

                if (!exceedsChainLimit && !exceedsAgeLimit)
                {
                    results.Add(Result(item, deleted: false, "retained"));
                    continue;
                }

                if (!request.DryRun)
                {
                    Directory.Delete(item.Path, recursive: true);
                }

                var reason = exceedsChainLimit && exceedsAgeLimit
                    ? "deleted-chain-limit-and-age"
                    : exceedsChainLimit ? "deleted-chain-limit" : "deleted-age";
                results.Add(Result(item, deleted: !request.DryRun, request.DryRun ? $"dry-run-{reason}" : reason));
            }
        }

        return results
            .OrderBy(result => result.VmName)
            .ThenBy(result => result.ChainId)
            .ToArray();
    }

    private static bool IsValidFullChain(BackupChainMetadata chain)
        => chain.Status == BackupStatus.Completed &&
           chain.RestorePoints.Any(point => point.BackupId == chain.FullBackupId && point.Type == BackupType.Full && point.Status == BackupStatus.Completed);

    private static RetentionResult Result((string Path, BackupChainMetadata Metadata) item, bool deleted, string reason, string? warning = null)
        => new(
            item.Metadata.ChainId,
            item.Metadata.VmId,
            item.Metadata.VmName,
            item.Path,
            deleted,
            reason,
            warning);
}
