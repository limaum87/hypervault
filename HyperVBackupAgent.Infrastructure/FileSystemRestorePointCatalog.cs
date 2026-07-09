using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class FileSystemRestorePointCatalog : IRestorePointCatalog
{
    private readonly string _backupRoot;
    private readonly IMetadataRepository _metadata;

    public FileSystemRestorePointCatalog(string backupRoot, IMetadataRepository metadata)
    {
        _backupRoot = Path.GetFullPath(backupRoot);
        _metadata = metadata;
    }

    public async Task<IReadOnlyList<RestorePointSummary>> ListRestorePointsAsync(string vmIdOrName, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_backupRoot))
        {
            return [];
        }

        var summaries = new List<RestorePointSummary>();
        foreach (var chainFile in Directory.EnumerateFiles(_backupRoot, "chain.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chainDirectory = Path.GetDirectoryName(chainFile)!;
            BackupChainMetadata chain;
            try
            {
                chain = await _metadata.LoadChainAsync(chainDirectory, cancellationToken);
            }
            catch
            {
                continue;
            }

            if (!MatchesVm(chain, vmIdOrName))
            {
                continue;
            }

            summaries.AddRange(chain.RestorePoints.Select(point => new RestorePointSummary(
                chain.ChainId,
                point.BackupId,
                point.Type,
                point.CreatedAt,
                point.Status,
                chainDirectory,
                point.ParentBackupId)));
        }

        return summaries
            .OrderBy(summary => summary.CreatedAt)
            .ToArray();
    }

    private static bool MatchesVm(BackupChainMetadata chain, string vmIdOrName)
        => string.Equals(chain.VmId, vmIdOrName, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(chain.VmName, vmIdOrName, StringComparison.OrdinalIgnoreCase);
}
