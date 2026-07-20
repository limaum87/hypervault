namespace HyperVBackupAgent.Core;

public enum BackupType
{
    Full,
    Incremental
}

public enum BackupStatus
{
    Pending,
    Completed,
    Failed
}

public sealed record VirtualMachineInfo(
    string Id,
    string Name,
    string State,
    int Generation,
    long MemoryBytes,
    IReadOnlyList<VirtualDiskInfo> Disks,
    IReadOnlyList<CheckpointInfo> Checkpoints);

public sealed record VirtualDiskInfo(
    string Id,
    string Path,
    long VirtualSizeBytes,
    long PhysicalSizeBytes);

public sealed record CheckpointInfo(
    string Id,
    string Name,
    DateTimeOffset CreatedAt,
    bool IsProduction);

public sealed record CheckpointCleanupResult(
    string VmId,
    string VmName,
    string CheckpointId,
    string CheckpointName,
    bool Removed,
    string? Error = null);

public sealed record ChangedRange(long Offset, long Length);

public sealed record RctDiskState(
    string DiskId,
    string StartChangeId,
    string EndChangeId,
    IReadOnlyList<ChangedRange> ChangedRanges);

public sealed record RctPreparationResult(
    bool IsReady,
    bool RequiresOfflineUpgrade,
    string Message,
    string? VmVersion = null,
    string? CheckpointId = null);

public sealed record BackupRequest(string VmNameOrId, string Destination);

public sealed record RestoreRequest(
    string RestorePoint,
    string Destination,
    string NewName,
    bool OverwriteExisting = false,
    string? TargetBackupId = null,
    bool CreateVm = true);

public sealed record FileLevelRestoreRequest(
    string RestorePoint,
    string? TargetBackupId = null,
    int? TtlMinutes = null);

public sealed record FileLevelRestoreVolume(
    string VolumeId,
    string MountPath,
    string? Label,
    string? FileSystem,
    long SizeBytes);

public sealed record FileLevelRestoreSession(
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<FileLevelRestoreVolume> Volumes);

public sealed record FileLevelRestoreEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long? SizeBytes,
    DateTimeOffset LastWriteTimeUtc);

public interface IFileLevelRestoreService
{
    Task<FileLevelRestoreSession> CreateSessionAsync(FileLevelRestoreRequest request, CancellationToken cancellationToken = default);
    FileLevelRestoreSession? GetSession(string sessionId);
    IReadOnlyList<FileLevelRestoreEntry> ListEntries(string sessionId, string volumeId, string? relativePath);
    string GetFilePath(string sessionId, string volumeId, string relativePath);
    Task<bool> CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default);
    Task CleanupOrphanedSessionsAsync(CancellationToken cancellationToken = default);
}

public sealed record BackupResult(
    string BackupId,
    string ChainId,
    BackupType Type,
    BackupStatus Status,
    string Path,
    string? Error = null);

public sealed record RestorePointSummary(
    string ChainId,
    string BackupId,
    BackupType Type,
    DateTimeOffset CreatedAt,
    BackupStatus Status,
    string ChainPath,
    string? ParentBackupId = null,
    string? VmId = null,
    string? VmName = null,
    long SizeBytes = 0,
    string? RestorePointPath = null);

public sealed record RetentionRequest(
    string BackupRoot,
    int KeepLastChains = 7,
    int? KeepDays = null,
    bool DryRun = false);

public sealed record RetentionResult(
    string ChainId,
    string VmId,
    string VmName,
    string ChainPath,
    bool Deleted,
    string Reason,
    string? Warning = null);

public sealed record VerifyResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record BackupChainMetadata
{
    public required string ChainId { get; init; }
    public required string VmId { get; init; }
    public required string VmName { get; init; }
    public required string SourceHost { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string LatestRestorePoint { get; set; }
    public required string FullBackupId { get; init; }
    public List<BackupMetadata> RestorePoints { get; init; } = [];
    public List<VirtualDiskInfo> Disks { get; init; } = [];
    public string RetentionPolicy { get; init; } = "keep-last-7";
    public BackupStatus Status { get; set; } = BackupStatus.Pending;
}

public sealed record BackupMetadata
{
    public required string BackupId { get; init; }
    public required BackupType Type { get; init; }
    public string? ParentBackupId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string VmId { get; init; }
    public required string VmName { get; init; }
    public List<BackupDiskMetadata> Disks { get; init; } = [];
    public Dictionary<string, string> Files { get; init; } = [];
    public Dictionary<string, string> Hashes { get; init; } = [];
    public long SizeBytes { get; set; }
    public Dictionary<string, string> RctReferenceIds { get; init; } = [];
    public Dictionary<string, IReadOnlyList<ChangedRange>> ChangedRanges { get; init; } = [];
    public string? StartChangeId { get; init; }
    public string? EndChangeId { get; init; }
    public BackupStatus Status { get; set; } = BackupStatus.Pending;
}

public sealed record BackupDiskMetadata(
    string DiskId,
    string SourcePath,
    string BackupFile,
    long LogicalSizeBytes,
    long PhysicalSizeBytes);
