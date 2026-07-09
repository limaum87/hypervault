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
}
