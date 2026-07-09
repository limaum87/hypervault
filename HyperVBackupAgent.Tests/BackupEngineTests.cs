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
    public async Task HashServiceComputesSha256()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "sample.txt");
        await File.WriteAllTextAsync(path, "abc");

        var hash = await new HashService().ComputeSha256Async(path);

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
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
            .BuildServiceProvider();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hvba-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
