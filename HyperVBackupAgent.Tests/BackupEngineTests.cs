using HyperVBackupAgent.Core;
using HyperVBackupAgent.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HyperVBackupAgent.Tests;

public sealed class BackupEngineTests
{
    [Fact]
    public async Task FullBackupCreatesChainAndVerifies()
    {
        var root = CreateTempDirectory();
        var vmRoot = Path.Combine(root, "vms", "ERP01");
        Directory.CreateDirectory(vmRoot);
        await File.WriteAllTextAsync(Path.Combine(vmRoot, "disk-0.bin"), "full-backup-content");

        var destination = Path.Combine(root, "backups");
        var services = BuildServices(Path.Combine(root, "vms"));

        var result = await services.GetRequiredService<IBackupEngine>()
            .RunFullBackupAsync(new BackupRequest("ERP01", destination));

        Assert.Equal(BackupStatus.Completed, result.Status);
        Assert.True(File.Exists(Path.Combine(result.Path, "chain.json")));

        var verify = await services.GetRequiredService<IVerifyEngine>().VerifyChainAsync(result.Path);
        Assert.True(verify.IsValid, string.Join(Environment.NewLine, verify.Errors));
    }

    [Fact]
    public async Task VerifyChainFailsWhenFileIsMissing()
    {
        var root = CreateTempDirectory();
        var vmRoot = Path.Combine(root, "vms", "ERP01");
        Directory.CreateDirectory(vmRoot);
        await File.WriteAllTextAsync(Path.Combine(vmRoot, "disk-0.bin"), "full-backup-content");

        var services = BuildServices(Path.Combine(root, "vms"));
        var result = await services.GetRequiredService<IBackupEngine>()
            .RunFullBackupAsync(new BackupRequest("ERP01", Path.Combine(root, "backups")));

        File.Delete(Directory.EnumerateFiles(Path.Combine(result.Path, "full")).First(path => path.EndsWith(".bin")));

        var verify = await services.GetRequiredService<IVerifyEngine>().VerifyChainAsync(result.Path);

        Assert.False(verify.IsValid);
        Assert.Contains(verify.Errors, error => error.Contains("Missing file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyRestoreReconstructsBackupInTemporaryDirectory()
    {
        var root = CreateTempDirectory();
        var vmRoot = Path.Combine(root, "vms", "ERP01");
        Directory.CreateDirectory(vmRoot);
        var sourceDisk = Path.Combine(vmRoot, "disk-0.bin");
        await File.WriteAllBytesAsync(sourceDisk, Enumerable.Range(0, 4096).Select(_ => (byte)'A').ToArray());

        var destination = Path.Combine(root, "backups");
        using var services = BuildServices(Path.Combine(root, "vms"));
        var full = await services.GetRequiredService<IBackupEngine>()
            .RunFullBackupAsync(new BackupRequest("ERP01", destination));

        await File.WriteAllBytesAsync(sourceDisk, Enumerable.Range(0, 4096).Select(_ => (byte)'B').ToArray());
        await services.GetRequiredService<IBackupEngine>()
            .RunIncrementalBackupAsync(new BackupRequest("ERP01", destination));

        var verify = await services.GetRequiredService<IVerifyEngine>()
            .VerifyRestoreAsync(full.Path, keepTemporaryFiles: false);

        Assert.True(verify.IsValid, string.Join(Environment.NewLine, verify.Errors));
        Assert.Contains(verify.Warnings, warning => warning.Contains("read-only mount validation was skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestoreAppliesIncrementalBlocks()
    {
        var root = CreateTempDirectory();
        var vmRoot = Path.Combine(root, "vms", "ERP01");
        Directory.CreateDirectory(vmRoot);
        var sourceDisk = Path.Combine(vmRoot, "disk-0.bin");
        await File.WriteAllBytesAsync(sourceDisk, Enumerable.Range(0, 4096).Select(_ => (byte)'A').ToArray());

        var destination = Path.Combine(root, "backups");
        var services = BuildServices(Path.Combine(root, "vms"));
        var full = await services.GetRequiredService<IBackupEngine>()
            .RunFullBackupAsync(new BackupRequest("ERP01", destination));

        await File.WriteAllBytesAsync(sourceDisk, Enumerable.Range(0, 4096).Select(_ => (byte)'B').ToArray());
        var incremental = await services.GetRequiredService<IBackupEngine>()
            .RunIncrementalBackupAsync(new BackupRequest("ERP01", destination));

        var restoreDestination = Path.Combine(root, "restore");
        await services.GetRequiredService<IRestoreEngine>()
            .RestoreAsync(new RestoreRequest(incremental.Path, restoreDestination, "ERP01-Restored"));

        var restoredDisk = Directory.EnumerateFiles(restoreDestination).Single();
        var restoredBytes = await File.ReadAllBytesAsync(restoredDisk);

        Assert.Equal(BackupStatus.Completed, full.Status);
        Assert.Equal(BackupStatus.Completed, incremental.Status);
        Assert.All(restoredBytes, value => Assert.Equal((byte)'B', value));
    }

    [Fact]
    public async Task RestoreCanTargetFullBackupBeforeIncrementals()
    {
        var root = CreateTempDirectory();
        var vmRoot = Path.Combine(root, "vms", "ERP01");
        Directory.CreateDirectory(vmRoot);
        var sourceDisk = Path.Combine(vmRoot, "disk-0.bin");
        await File.WriteAllBytesAsync(sourceDisk, Enumerable.Range(0, 4096).Select(_ => (byte)'A').ToArray());

        var destination = Path.Combine(root, "backups");
        var services = BuildServices(Path.Combine(root, "vms"));
        var full = await services.GetRequiredService<IBackupEngine>()
            .RunFullBackupAsync(new BackupRequest("ERP01", destination));

        await File.WriteAllBytesAsync(sourceDisk, Enumerable.Range(0, 4096).Select(_ => (byte)'B').ToArray());
        await services.GetRequiredService<IBackupEngine>()
            .RunIncrementalBackupAsync(new BackupRequest("ERP01", destination));

        var restoreDestination = Path.Combine(root, "restore-full");
        await services.GetRequiredService<IRestoreEngine>()
            .RestoreAsync(new RestoreRequest(full.Path, restoreDestination, "ERP01-Restored", TargetBackupId: full.BackupId));

        var restoredDisk = Directory.EnumerateFiles(restoreDestination).Single();
        var restoredBytes = await File.ReadAllBytesAsync(restoredDisk);

        Assert.All(restoredBytes, value => Assert.Equal((byte)'A', value));
    }

    [Fact]
    public async Task HashServiceComputesSha256()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(path, "abc");

        var hash = await new HashService().ComputeSha256Async(path);

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [Fact]
    public void ServiceCollectionCanSelectPowerShellHyperVProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperVBackupAgent:HyperVProvider"] = "PowerShell"
            })
            .Build();

        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddHyperVBackupAgent(configuration)
            .BuildServiceProvider();

        Assert.IsType<PowerShellHyperVService>(services.GetRequiredService<IHyperVService>());
    }

    [Fact]
    public void ServiceCollectionCanSelectNativeRctProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperVBackupAgent:RctProvider"] = "Native"
            })
            .Build();

        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddHyperVBackupAgent(configuration)
            .BuildServiceProvider();

        Assert.IsType<NativeHyperVRctService>(services.GetRequiredService<IRctService>());
    }

    [Fact]
    public async Task SimulatedCheckpointCleanupReturnsEmptyResult()
    {
        var root = CreateTempDirectory();
        using var services = BuildServices(Path.Combine(root, "vms"));

        var results = await services.GetRequiredService<IHyperVService>().CleanupTemporaryCheckpointsAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task RestorePointCatalogListsPointsForVm()
    {
        var root = CreateTempDirectory();
        var vmRoot = Path.Combine(root, "vms", "ERP01");
        Directory.CreateDirectory(vmRoot);
        await File.WriteAllTextAsync(Path.Combine(vmRoot, "disk-0.bin"), "full-backup-content");

        var destination = Path.Combine(root, "backups");
        using var services = BuildServices(Path.Combine(root, "vms"), destination);

        await services.GetRequiredService<IBackupEngine>()
            .RunFullBackupAsync(new BackupRequest("ERP01", destination));

        var restorePoints = await services.GetRequiredService<IRestorePointCatalog>()
            .ListRestorePointsAsync("ERP01");

        var restorePoint = Assert.Single(restorePoints);
        Assert.Equal(BackupType.Full, restorePoint.Type);
        Assert.Equal("ERP01", restorePoint.VmName);
        Assert.True(restorePoint.SizeBytes > 0);
        Assert.True(Directory.Exists(restorePoint.ChainPath));
        Assert.True(Directory.Exists(restorePoint.RestorePointPath));
    }

    [Fact]
    public async Task RetentionDeletesOldChainsButKeepsNewestValidFull()
    {
        var root = CreateTempDirectory();
        var backupRoot = Path.Combine(root, "backups");
        var repository = new JsonMetadataRepository();
        var oldChain = await CreateChainAsync(repository, backupRoot, "vm-1", "ERP01", "chain-old", DateTimeOffset.UtcNow.AddDays(-10), BackupStatus.Completed);
        var newChain = await CreateChainAsync(repository, backupRoot, "vm-1", "ERP01", "chain-new", DateTimeOffset.UtcNow, BackupStatus.Completed);
        var retention = new FileSystemRetentionService(repository);

        var results = await retention.ApplyRetentionAsync(new RetentionRequest(backupRoot, KeepLastChains: 1));

        Assert.False(Directory.Exists(oldChain));
        Assert.True(Directory.Exists(newChain));
        Assert.Contains(results, result => result.ChainId == "chain-old" && result.Deleted);
        Assert.Contains(results, result => result.ChainId == "chain-new" && result.Reason == "protected-last-valid-full");
    }

    [Fact]
    public async Task RetentionDoesNotDeleteIncompleteChainsAutomatically()
    {
        var root = CreateTempDirectory();
        var backupRoot = Path.Combine(root, "backups");
        var repository = new JsonMetadataRepository();
        var incompleteChain = await CreateChainAsync(repository, backupRoot, "vm-1", "ERP01", "chain-failed", DateTimeOffset.UtcNow.AddDays(-10), BackupStatus.Failed);
        await CreateChainAsync(repository, backupRoot, "vm-1", "ERP01", "chain-valid", DateTimeOffset.UtcNow, BackupStatus.Completed);
        var retention = new FileSystemRetentionService(repository);

        var results = await retention.ApplyRetentionAsync(new RetentionRequest(backupRoot, KeepLastChains: 1, KeepDays: 1));

        Assert.True(Directory.Exists(incompleteChain));
        Assert.Contains(results, result => result.ChainId == "chain-failed" && !result.Deleted && result.Warning is not null);
    }

    private static ServiceProvider BuildServices(string simulationRoot, string? backupRoot = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["HyperVBackupAgent:SimulationRoot"] = simulationRoot
        };
        if (backupRoot is not null)
        {
            settings["HyperVBackupAgent:BackupRoot"] = backupRoot;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddHyperVBackupAgent(configuration)
            .BuildServiceProvider();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hvba-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> CreateChainAsync(
        IMetadataRepository repository,
        string backupRoot,
        string vmId,
        string vmName,
        string chainId,
        DateTimeOffset createdAt,
        BackupStatus status)
    {
        var chainDirectory = Path.Combine(backupRoot, Environment.MachineName, vmId, chainId);
        var backupId = $"{chainId}-full";
        var backup = new BackupMetadata
        {
            BackupId = backupId,
            Type = BackupType.Full,
            CreatedAt = createdAt,
            VmId = vmId,
            VmName = vmName,
            Status = status
        };
        var chain = new BackupChainMetadata
        {
            ChainId = chainId,
            VmId = vmId,
            VmName = vmName,
            SourceHost = Environment.MachineName,
            CreatedAt = createdAt,
            LatestRestorePoint = backupId,
            FullBackupId = backupId,
            Status = status,
            RestorePoints = { backup }
        };

        await repository.SaveChainAsync(chainDirectory, chain);
        return chainDirectory;
    }
}
