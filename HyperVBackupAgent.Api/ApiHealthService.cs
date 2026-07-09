using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Api;

public sealed class ApiHealthService
{
    private readonly IConfiguration _configuration;
    private readonly IHyperVService _hyperV;

    public ApiHealthService(IConfiguration configuration, IHyperVService hyperV)
    {
        _configuration = configuration;
        _hyperV = hyperV;
    }

    public HealthCheckResult GetLive()
        => new("ok", DateTimeOffset.UtcNow, [new HealthCheckItem("process", "ok", "API process is running.")]);

    public async Task<HealthCheckResult> GetReadyAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<HealthCheckItem>
        {
            CheckApiToken(),
            CheckBackupRoot()
        };

        checks.Add(await CheckHyperVProviderAsync(cancellationToken));
        var status = checks.All(check => string.Equals(check.Status, "ok", StringComparison.OrdinalIgnoreCase))
            ? "ready"
            : "not_ready";

        return new HealthCheckResult(status, DateTimeOffset.UtcNow, checks);
    }

    private HealthCheckItem CheckApiToken()
    {
        var configuredToken = _configuration["HyperVBackupAgent:ApiToken"];
        return string.IsNullOrWhiteSpace(configuredToken)
            ? new HealthCheckItem("configuration.apiToken", "fail", "API token is not configured.")
            : new HealthCheckItem("configuration.apiToken", "ok", "API token is configured.");
    }

    private HealthCheckItem CheckBackupRoot()
    {
        var configured = _configuration["HyperVBackupAgent:BackupRoot"] ?? "backups";
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(configured);
        }
        catch (Exception ex)
        {
            return new HealthCheckItem("storage.backupRoot", "fail", $"Backup root path is invalid: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
        {
            return new HealthCheckItem("storage.backupRoot", "fail", $"Backup root does not exist: {fullPath}");
        }

        try
        {
            _ = Directory.EnumerateFileSystemEntries(fullPath).Take(1).ToArray();
            return new HealthCheckItem("storage.backupRoot", "ok", $"Backup root is accessible: {fullPath}");
        }
        catch (Exception ex)
        {
            return new HealthCheckItem("storage.backupRoot", "fail", $"Backup root is not accessible: {ex.Message}");
        }
    }

    private async Task<HealthCheckItem> CheckHyperVProviderAsync(CancellationToken cancellationToken)
    {
        try
        {
            var vms = await _hyperV.ListVmsAsync(cancellationToken);
            return new HealthCheckItem("provider.hyperV", "ok", $"Hyper-V provider initialized; {vms.Count} VM(s) visible.");
        }
        catch (Exception ex)
        {
            return new HealthCheckItem("provider.hyperV", "fail", $"Hyper-V provider check failed: {ex.Message}");
        }
    }
}

public sealed record HealthCheckResult(
    string Status,
    DateTimeOffset CheckedAt,
    IReadOnlyList<HealthCheckItem> Checks);

public sealed record HealthCheckItem(
    string Name,
    string Status,
    string Message);
