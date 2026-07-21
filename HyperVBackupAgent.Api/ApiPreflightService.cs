using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Api;

public sealed class ApiPreflightService
{
    private readonly IHyperVService _hyperV;
    private readonly IRctService _rct;
    private readonly IMetadataRepository _metadata;

    public ApiPreflightService(IHyperVService hyperV, IRctService rct, IMetadataRepository metadata)
    {
        _hyperV = hyperV;
        _rct = rct;
        _metadata = metadata;
    }

    public async Task<PreflightResult> CheckBackupAsync(BackupPreflightRequest request, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var details = new Dictionary<string, string>();

        var vm = await _hyperV.GetVmAsync(request.VmNameOrId, cancellationToken);
        if (vm is null)
        {
            errors.Add($"VM not found: {request.VmNameOrId}");
            return new PreflightResult(false, errors, warnings, details);
        }

        details["vmId"] = vm.Id;
        details["vmName"] = vm.Name;
        details["diskCount"] = vm.Disks.Count.ToString();

        if (!await _hyperV.SupportsProductionCheckpointAsync(vm.Id, cancellationToken))
        {
            errors.Add($"VM '{vm.Name}' does not support Production Checkpoints.");
        }

        ValidateDestination(request.Destination, errors, warnings, details);

        var estimatedBytes = vm.Disks.Sum(disk => Math.Max(disk.PhysicalSizeBytes, 0));
        details["estimatedBytes"] = estimatedBytes.ToString();
        ValidateFreeSpace(request.Destination, estimatedBytes, errors, warnings);

        if (request.Type == BackupType.Incremental)
        {
            await ValidateIncrementalRctAsync(vm, errors, warnings, details, cancellationToken);
        }

        return new PreflightResult(errors.Count == 0, errors, warnings, details);
    }

    private async Task ValidateIncrementalRctAsync(
        VirtualMachineInfo vm,
        List<string> errors,
        List<string> warnings,
        Dictionary<string, string> details,
        CancellationToken cancellationToken)
    {
        if (await CheckCheckpointRctAsync(vm, details, errors: null, cancellationToken))
        {
            return;
        }

        var preparation = await _hyperV.PrepareForRctAsync(vm.Id, cancellationToken);
        details["rctPreparationReady"] = preparation.IsReady.ToString();
        details["rctRequiresOfflineUpgrade"] = preparation.RequiresOfflineUpgrade.ToString();
        if (!string.IsNullOrWhiteSpace(preparation.VmVersion))
        {
            details["vmVersion"] = preparation.VmVersion;
        }

        if (!preparation.IsReady)
        {
            errors.Add(preparation.Message);
            return;
        }

        warnings.Add(preparation.Message);
        await CheckCheckpointRctAsync(vm, details, errors, cancellationToken);
    }

    private async Task<bool> CheckCheckpointRctAsync(
        VirtualMachineInfo vm,
        Dictionary<string, string> details,
        List<string>? errors,
        CancellationToken cancellationToken)
    {
        var checkpointId = await _hyperV.CreateProductionCheckpointAsync(vm.Id, $"HyperVBackupAgent-Preflight-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", cancellationToken);
        try
        {
            var consistentDisks = await _hyperV.GetCheckpointConsistentDisksAsync(vm.Id, checkpointId, cancellationToken);
            details["rctCheckedDiskCount"] = consistentDisks.Count.ToString();
            return await AllDisksHaveRctAsync(vm, consistentDisks, errors, cancellationToken);
        }
        finally
        {
            await _hyperV.RemoveCheckpointAsync(vm.Id, checkpointId, cancellationToken);
        }
    }

    private async Task<bool> AllDisksHaveRctAsync(
        VirtualMachineInfo vm,
        IReadOnlyList<VirtualDiskInfo> disks,
        List<string>? errors,
        CancellationToken cancellationToken)
    {
        var allAvailable = true;
        foreach (var disk in disks)
        {
            if (await _rct.IsAvailableAsync(vm, disk, cancellationToken))
            {
                continue;
            }

            allAvailable = false;
            errors?.Add($"RCT is not available for checkpoint-consistent disk '{disk.Id}'.");
        }

        return allAvailable;
    }

    public async Task<PreflightResult> CheckRestoreAsync(RestorePreflightRequest request, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var details = new Dictionary<string, string>();

        BackupChainMetadata? chain = null;
        try
        {
            chain = await _metadata.LoadChainAsync(request.RestorePoint, cancellationToken);
            details["chainId"] = chain.ChainId;
            details["vmId"] = chain.VmId;
            details["vmName"] = chain.VmName;
            details["restorePointCount"] = chain.RestorePoints.Count.ToString();
        }
        catch (Exception ex)
        {
            errors.Add($"Restore point could not be loaded: {ex.Message}");
        }

        ValidateDestination(request.Destination, errors, warnings, details);

        var estimatedBytes = chain?.RestorePoints.FirstOrDefault(point => point.Type == BackupType.Full)?.SizeBytes ?? 0;
        details["estimatedBytes"] = estimatedBytes.ToString();
        ValidateFreeSpace(request.Destination, estimatedBytes, errors, warnings);

        var existingVm = await _hyperV.GetVmAsync(request.NewName, cancellationToken);
        if (existingVm is not null && !request.OverwriteExisting)
        {
            errors.Add($"VM already exists: {request.NewName}");
        }

        return new PreflightResult(errors.Count == 0, errors, warnings, details);
    }

    private static void ValidateDestination(string destination, List<string> errors, List<string> warnings, Dictionary<string, string> details)
    {
        var fullPath = Path.GetFullPath(destination);
        details["destination"] = fullPath;

        if (File.Exists(fullPath))
        {
            errors.Add($"Destination points to a file: {fullPath}");
            return;
        }

        var existing = Directory.Exists(fullPath)
            ? fullPath
            : FindExistingParent(fullPath);

        if (existing is null)
        {
            errors.Add($"No existing parent directory found for destination: {fullPath}");
            return;
        }

        details["destinationExistingRoot"] = existing;
        if (!Directory.Exists(fullPath))
        {
            warnings.Add($"Destination directory does not exist yet and will need to be created: {fullPath}");
        }
    }

    private static void ValidateFreeSpace(string destination, long estimatedBytes, List<string> errors, List<string> warnings)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (string.IsNullOrWhiteSpace(root))
            {
                warnings.Add("Could not determine destination drive for free-space validation.");
                return;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                warnings.Add($"Destination drive is not ready: {root}");
                return;
            }

            if (estimatedBytes > 0 && drive.AvailableFreeSpace < estimatedBytes)
            {
                errors.Add($"Insufficient free space on {root}. Required at least {estimatedBytes} bytes, available {drive.AvailableFreeSpace} bytes.");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not validate free space: {ex.Message}");
        }
    }

    private static string? FindExistingParent(string path)
    {
        var directory = new DirectoryInfo(path);
        while (directory is not null)
        {
            if (directory.Exists)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

public sealed record BackupPreflightRequest(
    string VmNameOrId,
    string Destination,
    BackupType Type = BackupType.Full,
    string? SmbUsername = null,
    string? SmbPassword = null,
    string? SmbDomain = null)
{
    public SmbCredentials? SmbCredentials => SmbCredentials.From(SmbUsername, SmbPassword, SmbDomain);
}

public sealed record RestorePreflightRequest(
    string RestorePoint,
    string Destination,
    string NewName,
    bool OverwriteExisting = false);

public sealed record PreflightResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, string> Details);
