using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace HyperVaultManager.Models;

/// <summary>A Hyper-V host running the HyperVBackupAgent API.</summary>
[Index(nameof(Name), IsUnique = true)]
public class HyperVHost
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IpOrFqdn { get; set; } = string.Empty;
    public int Port { get; set; } = 5443;
    public bool UseHttps { get; set; } = true;
    /// <summary>Bearer token sent to the agent API. Stored server-side only.</summary>
    public string ApiToken { get; set; } = string.Empty;
    public string? CertificateFingerprint { get; set; }
    public string Status { get; set; } = HostStatuses.Unknown; // online/offline/unknown
    public string? AgentVersion { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<VirtualMachine> VirtualMachines { get; set; } = new();
}

public static class HostStatuses
{
    public const string Online = "online";
    public const string Offline = "offline";
    public const string Unknown = "unknown";
}

/// <summary>A VM discovered on a host via GET /vms.</summary>
[Index(nameof(HostId), nameof(ExternalId), IsUnique = true)]
public class VirtualMachine
{
    public int Id { get; set; }
    public int HostId { get; set; }
    public HyperVHost? Host { get; set; }
    public string ExternalId { get; set; } = string.Empty; // agent VM id (guid)
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int Generation { get; set; }
    public long MemoryBytes { get; set; }
    public long DiskSizeBytes { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Name) ? ExternalId : Name;
}

public static class StorageTypes
{
    public const string LocalPath = "local_path";
    public const string Smb = "smb";
}

/// <summary>A backup destination storage (local path or SMB share).</summary>
public class StorageTarget
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = StorageTypes.LocalPath; // local_path | smb
    public string Path { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class JobTypes
{
    public const string Full = "full";
    public const string Incremental = "incremental";
}

public static class CronPresets
{
    public const string DailyMidnight = "0 0 * * *";
    public const string Disabled = ""; // empty => manual only
}

/// <summary>A scheduled backup definition.</summary>
public class BackupJob
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int HostId { get; set; }
    public HyperVHost? Host { get; set; }
    public int VmId { get; set; }
    public VirtualMachine? Vm { get; set; }
    public int StorageId { get; set; }
    public StorageTarget? Storage { get; set; }
    public string Type { get; set; } = JobTypes.Full; // full | incremental
    /// <summary>Quartz-style cron expression. Empty means manual execution only.</summary>
    public string CronSchedule { get; set; } = CronPresets.Disabled;
    public int RetentionDays { get; set; } = 7;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class Roles
{
    public const string Admin = "admin";
    public const string User = "user";
}

/// <summary>A login account for the Manager web console.</summary>
[Index(nameof(Username), IsUnique = true)]
public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    /// <summary>PBKDF2 password hash in the form "iterations.base64(salt).base64(hash)".</summary>
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = Roles.User; // admin | user
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public static class RunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

/// <summary>A single backup execution tracked in history.</summary>
public class BackupRun
{
    public int Id { get; set; }
    public int? JobId { get; set; }
    public BackupJob? Job { get; set; }
    public int HostId { get; set; }
    public HyperVHost? Host { get; set; }
    public int VmId { get; set; }
    public VirtualMachine? Vm { get; set; }
    public int StorageId { get; set; }
    public StorageTarget? Storage { get; set; }
    public string Type { get; set; } = JobTypes.Full;
    public string Status { get; set; } = RunStatuses.Queued;
    public string? AgentJobId { get; set; }
    public string? CorrelationId { get; set; }
    public string? ResultPath { get; set; }
    public string? ChainId { get; set; }
    public string? BackupId { get; set; }
    public long SizeBytes { get; set; }
    public long DurationSeconds { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<VerificationRun> Verifications { get; set; } = new();
}

public static class VerifyKinds
{
    public const string Chain = "chain";
    public const string Restore = "restore";
}

/// <summary>A verify-chain / verify-restore execution.</summary>
public class VerificationRun
{
    public int Id { get; set; }
    public int? BackupRunId { get; set; }
    public BackupRun? BackupRun { get; set; }
    public int HostId { get; set; }
    public HyperVHost? Host { get; set; }
    public string Kind { get; set; } = VerifyKinds.Chain; // chain | restore
    public string TargetPath { get; set; } = string.Empty; // chainPath or restorePointPath
    public string Status { get; set; } = RunStatuses.Queued;
    public bool? IsValid { get; set; }
    public string? AgentJobId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Errors { get; set; }
    public string? Warnings { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>A restore execution.</summary>
public class RestoreRun
{
    public int Id { get; set; }
    public int? BackupRunId { get; set; }
    public BackupRun? BackupRun { get; set; }
    public int SourceHostId { get; set; }
    public int TargetHostId { get; set; }
    public HyperVHost? TargetHost { get; set; }
    public string RestorePointPath { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string NewName { get; set; } = string.Empty;
    public string? TargetBackupId { get; set; }
    public bool OverwriteExisting { get; set; }
    public string Status { get; set; } = RunStatuses.Queued;
    public string? AgentJobId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
