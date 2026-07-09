using HyperVBackupAgent.Api;
using HyperVBackupAgent.Core;
using Microsoft.Extensions.Configuration;

namespace HyperVBackupAgent.Tests;

public sealed class ApiHealthServiceTests
{
    [Fact]
    public async Task GetReadyAsyncReturnsReadyWhenRequiredChecksPass()
    {
        var backupRoot = CreateTempDirectory();
        var service = new ApiHealthService(CreateConfiguration("dev-token", backupRoot), new FakeHyperVService());

        var result = await service.GetReadyAsync();

        Assert.Equal("ready", result.Status);
        Assert.All(result.Checks, check => Assert.Equal("ok", check.Status));
    }

    [Fact]
    public async Task GetReadyAsyncReturnsNotReadyWhenConfigurationOrProviderFails()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "hvba-missing-root", Guid.NewGuid().ToString("N"));
        var service = new ApiHealthService(CreateConfiguration(string.Empty, missingRoot), new FakeHyperVService(failList: true));

        var result = await service.GetReadyAsync();

        Assert.Equal("not_ready", result.Status);
        Assert.Contains(result.Checks, check => check.Name == "configuration.apiToken" && check.Status == "fail");
        Assert.Contains(result.Checks, check => check.Name == "storage.backupRoot" && check.Status == "fail");
        Assert.Contains(result.Checks, check => check.Name == "provider.hyperV" && check.Status == "fail");
    }

    [Fact]
    public void GetLiveReturnsOk()
    {
        var service = new ApiHealthService(CreateConfiguration("dev-token", CreateTempDirectory()), new FakeHyperVService());

        var result = service.GetLive();

        Assert.Equal("ok", result.Status);
        Assert.Contains(result.Checks, check => check.Name == "process" && check.Status == "ok");
    }

    private static IConfiguration CreateConfiguration(string apiToken, string backupRoot)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperVBackupAgent:ApiToken"] = apiToken,
                ["HyperVBackupAgent:BackupRoot"] = backupRoot
            })
            .Build();

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hvba-health-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeHyperVService : IHyperVService
    {
        private readonly bool _failList;

        public FakeHyperVService(bool failList = false)
        {
            _failList = failList;
        }

        public Task<IReadOnlyList<VirtualMachineInfo>> ListVmsAsync(CancellationToken cancellationToken = default)
        {
            if (_failList)
            {
                throw new InvalidOperationException("provider unavailable");
            }

            IReadOnlyList<VirtualMachineInfo> vms =
            [
                new("vm-1", "ERP01", "Running", 2, 1024, [], [])
            ];
            return Task.FromResult(vms);
        }

        public Task<VirtualMachineInfo?> GetVmAsync(string nameOrId, CancellationToken cancellationToken = default)
            => Task.FromResult<VirtualMachineInfo?>(null);

        public Task<bool> SupportsProductionCheckpointAsync(string vmId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<string> CreateProductionCheckpointAsync(string vmId, string name, CancellationToken cancellationToken = default)
            => Task.FromResult("checkpoint-1");

        public Task<IReadOnlyList<VirtualDiskInfo>> GetCheckpointConsistentDisksAsync(string vmId, string checkpointId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VirtualDiskInfo>>([]);

        public Task RemoveCheckpointAsync(string vmId, string checkpointId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CheckpointCleanupResult>> CleanupTemporaryCheckpointsAsync(string namePrefix = "HyperVBackupAgent-", CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CheckpointCleanupResult>>([]);

        public Task CreateVmFromDisksAsync(string vmName, IReadOnlyList<string> diskPaths, bool overwriteExisting, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
