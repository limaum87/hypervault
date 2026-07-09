using HyperVBackupAgent.Api;
using HyperVBackupAgent.Core;
using HyperVBackupAgent.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HyperVBackupAgent.Tests;

public sealed class ApiPreflightServiceTests
{
    [Fact]
    public async Task BackupPreflightPassesForSimulatedVm()
    {
        var root = CreateTempDirectory();
        var vmRoot = Path.Combine(root, "vms", "ERP01");
        Directory.CreateDirectory(vmRoot);
        await File.WriteAllBytesAsync(Path.Combine(vmRoot, "disk-0.bin"), new byte[1024]);
        using var services = BuildServices(Path.Combine(root, "vms"));

        var result = await services.GetRequiredService<ApiPreflightService>()
            .CheckBackupAsync(new BackupPreflightRequest("ERP01", Path.Combine(root, "backups"), BackupType.Full));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("ERP01", result.Details["vmName"]);
        Assert.Contains(result.Warnings, warning => warning.Contains("does not exist yet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestorePreflightDetectsExistingVmConflict()
    {
        var root = CreateTempDirectory();
        var vmRoot = Path.Combine(root, "vms", "ERP01");
        Directory.CreateDirectory(vmRoot);
        await File.WriteAllBytesAsync(Path.Combine(vmRoot, "disk-0.bin"), new byte[1024]);
        using var services = BuildServices(Path.Combine(root, "vms"));
        var backup = await services.GetRequiredService<IBackupEngine>()
            .RunFullBackupAsync(new BackupRequest("ERP01", Path.Combine(root, "backups")));

        var result = await services.GetRequiredService<ApiPreflightService>()
            .CheckRestoreAsync(new RestorePreflightRequest(backup.Path, Path.Combine(root, "restore"), "ERP01"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("already exists", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IncrementalPreflightReportsOfflineUpgradeWhenRctPreparationCannotRunOnline()
    {
        var root = CreateTempDirectory();
        var service = new ApiPreflightService(
            new RctPreparationHyperVService(new RctPreparationResult(
                false,
                true,
                "VM configuration version 5.0 does not support Hyper-V RCT. Shut down the VM and run Update-VMVersion before using incremental backups.",
                "5.0")),
            new UnavailableRctService(),
            new JsonMetadataRepository());

        var result = await service.CheckBackupAsync(new BackupPreflightRequest(
            "ERP01",
            Path.Combine(root, "backups"),
            BackupType.Incremental));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Update-VMVersion", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("True", result.Details["rctRequiresOfflineUpgrade"]);
        Assert.Equal("5.0", result.Details["vmVersion"]);
    }

    private static ServiceProvider BuildServices(string simulationRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperVBackupAgent:SimulationRoot"] = simulationRoot
            })
            .Build();

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddHyperVBackupAgent(configuration)
            .AddSingleton<ApiPreflightService>()
            .BuildServiceProvider();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hvba-preflight-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class UnavailableRctService : IRctService
    {
        public Task<bool> IsAvailableAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<string> GetCurrentChangeIdAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("RCT unavailable");

        public Task<RctDiskState> GetChangedRangesAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, string previousChangeId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("RCT unavailable");
    }

    private sealed class RctPreparationHyperVService : IHyperVService
    {
        private readonly RctPreparationResult _preparation;

        public RctPreparationHyperVService(RctPreparationResult preparation)
        {
            _preparation = preparation;
        }

        public Task<IReadOnlyList<VirtualMachineInfo>> ListVmsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VirtualMachineInfo>>([CreateVm()]);

        public Task<VirtualMachineInfo?> GetVmAsync(string nameOrId, CancellationToken cancellationToken = default)
            => Task.FromResult<VirtualMachineInfo?>(CreateVm());

        public Task<bool> SupportsProductionCheckpointAsync(string vmId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<string> CreateProductionCheckpointAsync(string vmId, string name, CancellationToken cancellationToken = default)
            => Task.FromResult("checkpoint-1");

        public Task<IReadOnlyList<VirtualDiskInfo>> GetCheckpointConsistentDisksAsync(string vmId, string checkpointId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VirtualDiskInfo>>(CreateVm().Disks);

        public Task RemoveCheckpointAsync(string vmId, string checkpointId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<RctPreparationResult> PrepareForRctAsync(string vmId, CancellationToken cancellationToken = default)
            => Task.FromResult(_preparation);

        public Task<IReadOnlyList<CheckpointCleanupResult>> CleanupTemporaryCheckpointsAsync(string namePrefix = "HyperVBackupAgent-", CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CheckpointCleanupResult>>([]);

        public Task CreateVmFromDisksAsync(string vmName, IReadOnlyList<string> diskPaths, bool overwriteExisting, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static VirtualMachineInfo CreateVm()
            => new(
                "vm-1",
                "ERP01",
                "Running",
                2,
                1024,
                [new VirtualDiskInfo("disk-0", "C:\\vms\\disk.vhdx", 1024, 1024)],
                []);
    }
}
