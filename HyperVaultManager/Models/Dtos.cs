using HyperVaultManager.Models;

namespace HyperVaultManager.Dtos;

// ---- Hosts ----
public record HostCreateDto(
    string Name,
    string IpOrFqdn,
    int Port,
    bool UseHttps,
    string ApiToken,
    string? CertificateFingerprint,
    string? Notes);

public record HostUpdateDto(
    string Name,
    string IpOrFqdn,
    int Port,
    bool UseHttps,
    string? ApiToken,        // null => keep existing token
    string? CertificateFingerprint,
    string? Notes);

public record HostViewDto(
    int Id,
    string Name,
    string IpOrFqdn,
    int Port,
    bool UseHttps,
    string Status,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    string? Notes,
    bool HasToken,
    int VmCount);

// ---- Storages ----
public record StorageCreateDto(string Name, string Type, string Path, string? Notes);
public record StorageViewDto(int Id, string Name, string Type, string Path, string? Notes, DateTimeOffset CreatedAt);

// ---- VMs ----
public record VmViewDto(
    int Id,
    int HostId,
    string HostName,
    string ExternalId,
    string Name,
    string State,
    int Generation,
    long MemoryBytes,
    long DiskSizeBytes,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? LastBackupAt,
    string? LastBackupStatus);

// ---- Jobs ----
public record JobCreateDto(
    string Name,
    int HostId,
    int VmId,
    int StorageId,
    string Type,
    string CronSchedule,
    int RetentionDays,
    bool Enabled);

public record JobViewDto(
    int Id,
    string Name,
    int HostId,
    string HostName,
    int VmId,
    string VmName,
    int StorageId,
    string StorageName,
    string Type,
    string CronSchedule,
    int RetentionDays,
    bool Enabled,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    DateTimeOffset CreatedAt);

// ---- Manual operations ----
public record ManualBackupDto(int StorageId, string Type, int? JobId);

public record VerifyDto(int HostId, string Kind, string TargetPath, int? BackupRunId);

public record RestoreDto(
    int SourceHostId,
    int TargetHostId,
    string RestorePointPath,
    string Destination,
    string NewName,
    string? TargetBackupId,
    bool OverwriteExisting,
    int? BackupRunId);

// ---- Backup history / runs ----
public record BackupRunViewDto(
    int Id,
    int? JobId,
    string? JobName,
    int HostId,
    string HostName,
    int VmId,
    string VmName,
    int StorageId,
    string StorageName,
    string Type,
    string Status,
    string? AgentJobId,
    string? CorrelationId,
    string? ResultPath,
    string? ChainId,
    string? BackupId,
    long SizeBytes,
    long DurationSeconds,
    string? Message,
    string? Error,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public record VerificationRunViewDto(
    int Id,
    int? BackupRunId,
    int HostId,
    string HostName,
    string Kind,
    string TargetPath,
    string Status,
    bool? IsValid,
    string? AgentJobId,
    string? CorrelationId,
    string? Errors,
    string? Warnings,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public record RestoreRunViewDto(
    int Id,
    int? BackupRunId,
    int SourceHostId,
    int TargetHostId,
    string TargetHostName,
    string NewName,
    string Destination,
    string Status,
    string? AgentJobId,
    string? CorrelationId,
    string? Error,
    string? Message,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

// ---- Dashboard ----
// ---- Auth / users ----
public record LoginDto(string Username, string Password);
public record MeDto(int Id, string Username, string Role, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);
public record UserCreateDto(string Username, string Password, string Role, bool Enabled);
public record UserUpdateDto(string Username, string Role, bool Enabled);
public record ResetPasswordDto(string Password);
public record ChangePasswordDto(string CurrentPassword, string NewPassword);

public record DashboardDto(
    int TotalHosts,
    int OnlineHosts,
    int OfflineHosts,
    int TotalVms,
    int VmsWithoutBackup,
    int BackupsLast24h,
    int FailedBackupsLast24h,
    long EstimatedStorageBytes,
    List<BackupRunViewDto> RecentBackups,
    List<BackupRunViewDto> RecentFailures);

public record ApiError(string Code, string Message, string? TraceId = null);
