namespace HyperVBackupAgent.Core;

public interface IHyperVService
{
    Task<IReadOnlyList<VirtualMachineInfo>> ListVmsAsync(CancellationToken cancellationToken = default);
    Task<VirtualMachineInfo?> GetVmAsync(string nameOrId, CancellationToken cancellationToken = default);
    Task<bool> SupportsProductionCheckpointAsync(string vmId, CancellationToken cancellationToken = default);
    Task<string> CreateProductionCheckpointAsync(string vmId, string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VirtualDiskInfo>> GetCheckpointConsistentDisksAsync(string vmId, string checkpointId, CancellationToken cancellationToken = default);
    Task RemoveCheckpointAsync(string vmId, string checkpointId, CancellationToken cancellationToken = default);
    Task<RctPreparationResult> PrepareForRctAsync(string vmId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CheckpointCleanupResult>> CleanupTemporaryCheckpointsAsync(string namePrefix = "HyperVBackupAgent-", CancellationToken cancellationToken = default);
    Task CreateVmFromDisksAsync(string vmName, IReadOnlyList<string> diskPaths, bool overwriteExisting, CancellationToken cancellationToken = default);
}

public interface IRctService
{
    Task<bool> IsAvailableAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, CancellationToken cancellationToken = default);
    Task<string> GetCurrentChangeIdAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, CancellationToken cancellationToken = default);
    Task<RctDiskState> GetChangedRangesAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, string previousChangeId, CancellationToken cancellationToken = default);
}

public interface IBackupEngine
{
    Task<BackupResult> RunFullBackupAsync(BackupRequest request, CancellationToken cancellationToken = default);
    Task<BackupResult> RunIncrementalBackupAsync(BackupRequest request, CancellationToken cancellationToken = default);
}

public interface IRestoreEngine
{
    Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default);
}

public interface IVerifyEngine
{
    Task<VerifyResult> VerifyChainAsync(string chainPath, CancellationToken cancellationToken = default);
    Task<VerifyResult> VerifyRestoreAsync(string restorePointPath, bool keepTemporaryFiles, CancellationToken cancellationToken = default);
}

public interface IStorageProvider
{
    Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default);
    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
}

public interface IMetadataRepository
{
    Task SaveChainAsync(string chainDirectory, BackupChainMetadata chain, CancellationToken cancellationToken = default);
    Task<BackupChainMetadata> LoadChainAsync(string chainDirectory, CancellationToken cancellationToken = default);
}

public interface IRestorePointCatalog
{
    Task<IReadOnlyList<RestorePointSummary>> ListRestorePointsAsync(
        string vmIdOrName,
        BackupStatus? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);
}

public interface IRetentionService
{
    Task<IReadOnlyList<RetentionResult>> ApplyRetentionAsync(RetentionRequest request, CancellationToken cancellationToken = default);
}

public interface IHashService
{
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default);
}
