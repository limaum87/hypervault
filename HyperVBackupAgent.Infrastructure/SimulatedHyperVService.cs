using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class SimulatedHyperVService : IHyperVService
{
    private readonly string _root;

    public SimulatedHyperVService(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public Task<IReadOnlyList<VirtualMachineInfo>> ListVmsAsync(CancellationToken cancellationToken = default)
    {
        var vms = Directory.EnumerateDirectories(_root)
            .Select(CreateVmInfo)
            .ToArray();

        return Task.FromResult<IReadOnlyList<VirtualMachineInfo>>(vms);
    }

    public async Task<VirtualMachineInfo?> GetVmAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        var vms = await ListVmsAsync(cancellationToken);
        return vms.FirstOrDefault(vm =>
            string.Equals(vm.Name, nameOrId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(vm.Id, nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> SupportsProductionCheckpointAsync(string vmId, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<string> CreateProductionCheckpointAsync(string vmId, string name, CancellationToken cancellationToken = default)
        => Task.FromResult($"sim-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");

    public async Task<IReadOnlyList<VirtualDiskInfo>> GetCheckpointConsistentDisksAsync(string vmId, string checkpointId, CancellationToken cancellationToken = default)
    {
        var vm = await GetVmAsync(vmId, cancellationToken)
            ?? throw new InvalidOperationException($"VM '{vmId}' was not found.");
        return vm.Disks;
    }

    public Task RemoveCheckpointAsync(string vmId, string checkpointId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<RctPreparationResult> PrepareForRctAsync(string vmId, CancellationToken cancellationToken = default)
        => Task.FromResult(new RctPreparationResult(true, false, "Simulated VM is ready for RCT.", "8.0"));

    public Task<IReadOnlyList<CheckpointCleanupResult>> CleanupTemporaryCheckpointsAsync(string namePrefix = "HyperVBackupAgent-", CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CheckpointCleanupResult>>([]);

    public Task CreateVmFromDisksAsync(string vmName, IReadOnlyList<string> diskPaths, bool overwriteExisting, CancellationToken cancellationToken = default)
    {
        if (!overwriteExisting && Directory.Exists(Path.Combine(_root, vmName)))
        {
            throw new InvalidOperationException($"VM '{vmName}' already exists. Use overwriteExisting=true explicitly.");
        }

        return Task.CompletedTask;
    }

    private static VirtualMachineInfo CreateVmInfo(string directory)
    {
        var disks = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            .Select((path, index) =>
            {
                var file = new FileInfo(path);
                return new VirtualDiskInfo($"disk-{index}", file.FullName, file.Length, file.Length);
            })
            .ToArray();

        var name = Path.GetFileName(directory);
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();
        return new VirtualMachineInfo(id, name, "Running", 2, 4L * 1024 * 1024 * 1024, disks, []);
    }
}
