using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class VerifyEngine : IVerifyEngine
{
    private readonly IMetadataRepository _metadata;
    private readonly IHashService _hash;

    public VerifyEngine(IMetadataRepository metadata, IHashService hash)
    {
        _metadata = metadata;
        _hash = hash;
    }

    public async Task<VerifyResult> VerifyChainAsync(string chainPath, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        BackupChainMetadata chain;
        try
        {
            chain = await _metadata.LoadChainAsync(chainPath, cancellationToken);
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, [$"Could not load chain.json: {ex.Message}"], warnings);
        }

        if (chain.RestorePoints.Count == 0)
        {
            errors.Add("Chain has no restore points.");
        }

        if (chain.RestorePoints.FirstOrDefault()?.Type != BackupType.Full)
        {
            errors.Add("First restore point must be a full backup.");
        }

        string? previousId = null;
        foreach (var point in chain.RestorePoints)
        {
            if (point.Type == BackupType.Incremental && point.ParentBackupId != previousId)
            {
                errors.Add($"Restore point {point.BackupId} has invalid parent_backup_id.");
            }

            foreach (var file in point.Files)
            {
                var path = Path.Combine(chainPath, file.Value);
                if (!File.Exists(path))
                {
                    errors.Add($"Missing file for {point.BackupId}/{file.Key}: {file.Value}");
                    continue;
                }

                if (point.Hashes.TryGetValue(file.Key, out var expected))
                {
                    var actual = await _hash.ComputeSha256Async(path, cancellationToken);
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"Hash mismatch for {file.Value}.");
                    }
                }
                else
                {
                    warnings.Add($"Missing hash entry for {point.BackupId}/{file.Key}.");
                }
            }

            previousId = point.BackupId;
        }

        return new VerifyResult(errors.Count == 0, errors, warnings);
    }

    public async Task<VerifyResult> VerifyRestoreAsync(string restorePointPath, bool keepTemporaryFiles, CancellationToken cancellationToken = default)
    {
        var chainResult = await VerifyChainAsync(restorePointPath, cancellationToken);
        return chainResult with { Warnings = chainResult.Warnings.Concat(["VHDX mount validation is not implemented in this MVP scaffold."]).ToArray() };
    }
}
