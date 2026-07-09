using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class VerifyEngine : IVerifyEngine
{
    private readonly IMetadataRepository _metadata;
    private readonly IHashService _hash;
    private readonly RestoreMaterializer _materializer;
    private readonly VhdReadOnlyMountValidator _mountValidator;

    public VerifyEngine(
        IMetadataRepository metadata,
        IHashService hash,
        RestoreMaterializer materializer,
        VhdReadOnlyMountValidator mountValidator)
    {
        _metadata = metadata;
        _hash = hash;
        _materializer = materializer;
        _mountValidator = mountValidator;
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
        if (!chainResult.IsValid)
        {
            return chainResult;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "hvbackup-agent-verify", Guid.NewGuid().ToString("N"));
        var warnings = chainResult.Warnings.ToList();
        try
        {
            var materialized = await _materializer.MaterializeAsync(
                new RestoreRequest(restorePointPath, tempDirectory, "verify-restore", OverwriteExisting: true),
                cancellationToken);

            if (materialized.DiskPaths.Count == 0)
            {
                return chainResult with { IsValid = false, Errors = ["Restore verification produced no disks."] };
            }

            foreach (var diskPath in materialized.DiskPaths)
            {
                if (!File.Exists(diskPath))
                {
                    return chainResult with { IsValid = false, Errors = [$"Restore verification did not produce expected disk: {diskPath}"] };
                }
            }

            warnings.AddRange(await _mountValidator.ValidateAsync(materialized.DiskPaths, cancellationToken));
            if (keepTemporaryFiles)
            {
                warnings.Add($"Temporary restore verification files kept at {tempDirectory}.");
            }

            return chainResult with { Warnings = warnings };
        }
        catch (Exception ex)
        {
            return chainResult with { IsValid = false, Errors = [$"Restore verification failed: {ex.Message}"], Warnings = warnings };
        }
        finally
        {
            if (!keepTemporaryFiles && Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
