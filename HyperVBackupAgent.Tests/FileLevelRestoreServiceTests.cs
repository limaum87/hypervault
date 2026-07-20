using System.Text.Json;
using HyperVBackupAgent.Core;
using HyperVBackupAgent.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace HyperVBackupAgent.Tests;

public sealed class FileLevelRestoreServiceTests
{
    [Fact]
    public async Task SessionRestrictsBrowsingToItsMountedVolumeAndCleansUp()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvba-flr-tests", Guid.NewGuid().ToString("N"));
        var volumeRoot = Path.Combine(root, "mounted-volume");
        Directory.CreateDirectory(volumeRoot);
        await File.WriteAllTextAsync(Path.Combine(volumeRoot, "document.txt"), "restored content");
        Directory.CreateDirectory(Path.Combine(volumeRoot, "folder"));

        var chainPath = await CreateChainAsync(root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperVBackupAgent:FileLevelRestore:TemporaryRoot"] = Path.Combine(root, "sessions"),
                ["HyperVBackupAgent:FileLevelRestore:TtlMinutes"] = "60"
            })
            .Build();
        var powerShell = new FakePowerShellRunner(volumeRoot);
        var service = new FileLevelRestoreService(new RestoreMaterializer(new JsonMetadataRepository()), powerShell, configuration);

        var session = await service.CreateSessionAsync(new FileLevelRestoreRequest(chainPath));

        var volume = Assert.Single(session.Volumes);
        var entries = service.ListEntries(session.SessionId, volume.VolumeId, "");
        Assert.Contains(entries, entry => entry.Name == "document.txt" && !entry.IsDirectory);
        Assert.Contains(entries, entry => entry.Name == "folder" && entry.IsDirectory);
        Assert.Equal(Path.Combine(volumeRoot, "document.txt"), service.GetFilePath(session.SessionId, volume.VolumeId, "document.txt"));
        Assert.Throws<ArgumentException>(() => service.GetFilePath(session.SessionId, volume.VolumeId, "..\\outside.txt"));

        Assert.True(await service.CloseSessionAsync(session.SessionId));
        Assert.Null(service.GetSession(session.SessionId));
        Assert.True(powerShell.DismountCalls > 0);
    }

    private static async Task<string> CreateChainAsync(string root)
    {
        var chainPath = Path.Combine(root, "chain-test");
        var fullDirectory = Path.Combine(chainPath, "full");
        Directory.CreateDirectory(fullDirectory);
        await File.WriteAllTextAsync(Path.Combine(fullDirectory, "disk-0.bin"), "backup disk");
        var backup = new BackupMetadata
        {
            BackupId = "full-1",
            Type = BackupType.Full,
            CreatedAt = DateTimeOffset.UtcNow,
            VmId = "vm-1",
            VmName = "VM 1",
            Status = BackupStatus.Completed,
            Disks = [new BackupDiskMetadata("disk-0", "disk-0.bin", "full/disk-0.bin", 11, 11)]
        };
        var chain = new BackupChainMetadata
        {
            ChainId = "chain-test",
            VmId = "vm-1",
            VmName = "VM 1",
            SourceHost = "host",
            CreatedAt = DateTimeOffset.UtcNow,
            LatestRestorePoint = backup.BackupId,
            FullBackupId = backup.BackupId,
            Status = BackupStatus.Completed,
            RestorePoints = [backup]
        };
        await new JsonMetadataRepository().SaveChainAsync(chainPath, chain);
        return chainPath;
    }

    private sealed class FakePowerShellRunner : IPowerShellRunner
    {
        private readonly string _mountPath;
        public int DismountCalls { get; private set; }

        public FakePowerShellRunner(string mountPath) => _mountPath = mountPath;

        public Task<PowerShellResult> RunAsync(string script, CancellationToken cancellationToken = default)
        {
            if (script.Contains("Dismount-VHD", StringComparison.Ordinal))
            {
                DismountCalls++;
                return Task.FromResult(new PowerShellResult(0, string.Empty, string.Empty));
            }

            var output = JsonSerializer.Serialize(new[]
            {
                new { diskPath = "restored.vhdx", partitionNumber = 1, mountPath = _mountPath, label = "Restored", fileSystem = "NTFS", sizeBytes = 1024L }
            });
            return Task.FromResult(new PowerShellResult(0, output, string.Empty));
        }
    }
}
