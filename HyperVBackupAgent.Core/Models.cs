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

public sealed record ChangedRange(long Offset, long Length);

public sealed record RctDiskState(
    string DiskId,
    string StartChangeId,
    string EndChangeId,
    IReadOnlyList<ChangedRange> ChangedRanges);

public sealed record BackupRequest(string VmNameOrId, string Destination);

public sealed record RestoreRequest(
    string RestorePoint,
    string Destination,
    string NewName,
    bool OverwriteExisting = false);

public sealed record BackupResult(
    string BackupId,
    string ChainId,
    BackupType Type,
    BackupStatus Status,
    string Path,
    string? Error = null);

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
