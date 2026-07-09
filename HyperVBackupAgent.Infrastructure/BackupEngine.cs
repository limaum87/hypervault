using System.Text.Json;
using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class BackupEngine : IBackupEngine
{
    private readonly IHyperVService _hyperV;
    private readonly IRctService _rct;
    private readonly IStorageProvider _storage;
    private readonly IMetadataRepository _metadata;
    private readonly IHashService _hash;
    private readonly string _hostName;

    public BackupEngine(IHyperVService hyperV, IRctService rct, IStorageProvider storage, IMetadataRepository metadata, IHashService hash)
    {
        _hyperV = hyperV;
        _rct = rct;
        _storage = storage;
        _metadata = metadata;
        _hash = hash;
        _hostName = Environment.MachineName;
    }

    public async Task<BackupResult> RunFullBackupAsync(BackupRequest request, CancellationToken cancellationToken = default)
    {
        var vm = await ResolveVmAsync(request.VmNameOrId, cancellationToken);
        var chainId = $"chain-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var backupId = $"full-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var chainDirectory = Path.Combine(Path.GetFullPath(request.Destination), _hostName, vm.Id, chainId);
        var fullDirectory = Path.Combine(chainDirectory, "full");

        await _storage.EnsureDirectoryAsync(fullDirectory, cancellationToken);
        string? checkpointId = null;
        try
        {
            checkpointId = await _hyperV.CreateProductionCheckpointAsync(vm.Id, $"HyperVBackupAgent-{backupId}", cancellationToken);
            var consistentDisks = await _hyperV.GetCheckpointConsistentDisksAsync(vm.Id, checkpointId, cancellationToken);
            var backup = new BackupMetadata
            {
                BackupId = backupId,
                Type = BackupType.Full,
                CreatedAt = DateTimeOffset.UtcNow,
                VmId = vm.Id,
                VmName = vm.Name,
                Status = BackupStatus.Completed
            };

            foreach (var disk in consistentDisks)
            {
                var fileName = $"{disk.Id}.full{Path.GetExtension(disk.Path)}";
                var destination = Path.Combine(fullDirectory, fileName);
                await _storage.CopyFileAsync(disk.Path, destination, cancellationToken);
                var hash = await _hash.ComputeSha256Async(destination, cancellationToken);
                var changeId = await _rct.GetCurrentChangeIdAsync(vm, disk, cancellationToken);
                backup.Disks.Add(new BackupDiskMetadata(disk.Id, disk.Path, Path.Combine("full", fileName), disk.VirtualSizeBytes, disk.PhysicalSizeBytes));
                backup.Files[disk.Id] = Path.Combine("full", fileName);
                backup.Hashes[disk.Id] = hash;
                backup.RctReferenceIds[disk.Id] = changeId;
                backup.SizeBytes += new FileInfo(destination).Length;
            }

            await WriteJsonAsync(Path.Combine(fullDirectory, "metadata.json"), backup, cancellationToken);

            var chain = new BackupChainMetadata
            {
                ChainId = chainId,
                VmId = vm.Id,
                VmName = vm.Name,
                SourceHost = _hostName,
                CreatedAt = DateTimeOffset.UtcNow,
                LatestRestorePoint = backupId,
                FullBackupId = backupId,
                Status = BackupStatus.Completed,
                Disks = consistentDisks.ToList(),
                RestorePoints = { backup }
            };

            await _metadata.SaveChainAsync(chainDirectory, chain, cancellationToken);
            return new BackupResult(backupId, chainId, BackupType.Full, BackupStatus.Completed, chainDirectory);
        }
        catch (Exception ex)
        {
            return new BackupResult(backupId, chainId, BackupType.Full, BackupStatus.Failed, chainDirectory, ex.Message);
        }
        finally
        {
            if (checkpointId is not null)
            {
                await _hyperV.RemoveCheckpointAsync(vm.Id, checkpointId, cancellationToken);
            }
        }
    }

    public async Task<BackupResult> RunIncrementalBackupAsync(BackupRequest request, CancellationToken cancellationToken = default)
    {
        var vm = await ResolveVmAsync(request.VmNameOrId, cancellationToken);
        var vmRoot = Path.Combine(Path.GetFullPath(request.Destination), _hostName, vm.Id);
        var chainDirectory = Directory.EnumerateDirectories(vmRoot, "chain-*").OrderBy(path => path).LastOrDefault()
            ?? throw new InvalidOperationException($"No full backup chain found for VM '{vm.Name}'.");

        var chain = await _metadata.LoadChainAsync(chainDirectory, cancellationToken);
        var parent = chain.RestorePoints.Last();
        var incNumber = chain.RestorePoints.Count(point => point.Type == BackupType.Incremental) + 1;
        var backupId = $"inc-{incNumber:0000}";
        var incDirectory = Path.Combine(chainDirectory, "increments", backupId);

        await _storage.EnsureDirectoryAsync(incDirectory, cancellationToken);
        string? checkpointId = null;
        try
        {
            checkpointId = await _hyperV.CreateProductionCheckpointAsync(vm.Id, $"HyperVBackupAgent-{backupId}", cancellationToken);
            var consistentDisks = await _hyperV.GetCheckpointConsistentDisksAsync(vm.Id, checkpointId, cancellationToken);
            var backup = new BackupMetadata
            {
                BackupId = backupId,
                Type = BackupType.Incremental,
                ParentBackupId = parent.BackupId,
                CreatedAt = DateTimeOffset.UtcNow,
                VmId = vm.Id,
                VmName = vm.Name,
                Status = BackupStatus.Completed
            };

            foreach (var disk in consistentDisks)
            {
                var previousChangeId = parent.RctReferenceIds.GetValueOrDefault(disk.Id) ?? parent.EndChangeId ?? string.Empty;
                var rctState = await _rct.GetChangedRangesAsync(vm, disk, previousChangeId, cancellationToken);
                var fileName = $"{disk.Id}.blocks";
                var destination = Path.Combine(incDirectory, fileName);
                await WriteChangedBlocksAsync(disk.Path, destination, rctState.ChangedRanges, cancellationToken);
                backup.Disks.Add(new BackupDiskMetadata(disk.Id, disk.Path, Path.Combine("increments", backupId, fileName), disk.VirtualSizeBytes, disk.PhysicalSizeBytes));
                backup.Files[disk.Id] = Path.Combine("increments", backupId, fileName);
                backup.Hashes[disk.Id] = await _hash.ComputeSha256Async(destination, cancellationToken);
                backup.ChangedRanges[disk.Id] = rctState.ChangedRanges;
                backup.RctReferenceIds[disk.Id] = rctState.EndChangeId;
                backup.SizeBytes += new FileInfo(destination).Length;
            }

            await WriteJsonAsync(Path.Combine(incDirectory, "inc.json"), backup, cancellationToken);
            chain.RestorePoints.Add(backup);
            chain.LatestRestorePoint = backupId;
            await _metadata.SaveChainAsync(chainDirectory, chain, cancellationToken);
            return new BackupResult(backupId, chain.ChainId, BackupType.Incremental, BackupStatus.Completed, chainDirectory);
        }
        catch (Exception ex)
        {
            return new BackupResult(backupId, chain.ChainId, BackupType.Incremental, BackupStatus.Failed, chainDirectory, ex.Message);
        }
        finally
        {
            if (checkpointId is not null)
            {
                await _hyperV.RemoveCheckpointAsync(vm.Id, checkpointId, cancellationToken);
            }
        }
    }

    private async Task<VirtualMachineInfo> ResolveVmAsync(string nameOrId, CancellationToken cancellationToken)
        => await _hyperV.GetVmAsync(nameOrId, cancellationToken)
            ?? throw new InvalidOperationException($"VM '{nameOrId}' was not found.");

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }, cancellationToken);
    }

    private static async Task WriteChangedBlocksAsync(string sourcePath, string destinationPath, IReadOnlyList<ChangedRange> ranges, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = File.OpenRead(sourcePath);
        await using var destination = File.Create(destinationPath);
        await using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(ranges.Count);
        foreach (var range in ranges)
        {
            writer.Write(range.Offset);
            writer.Write(range.Length);
            source.Position = range.Offset;
            var buffer = new byte[range.Length];
            var read = await source.ReadAsync(buffer, cancellationToken);
            writer.Write(read);
            writer.Write(buffer.AsSpan(0, read));
        }
    }
}
