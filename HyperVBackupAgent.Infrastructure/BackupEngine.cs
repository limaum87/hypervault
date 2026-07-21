using System.Text.Json;
using HyperVBackupAgent.Core;
using System.Diagnostics;
using System.Text;

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
            await EnsureRctReadyAsync(vm, consistentDisks, cancellationToken);
            var rctReferenceIds = new Dictionary<string, string>();
            foreach (var disk in consistentDisks)
            {
                rctReferenceIds[disk.Id] = await _rct.GetCurrentChangeIdAsync(vm, disk, cancellationToken);
            }

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
                var fileName = $"{ToSafeFileName(disk.Id)}.full{Path.GetExtension(disk.Path)}";
                var destination = Path.Combine(fullDirectory, fileName);
                await _storage.CopyFileAsync(disk.Path, destination, cancellationToken);
                var hash = await _hash.ComputeSha256Async(destination, cancellationToken);
                backup.Disks.Add(new BackupDiskMetadata(disk.Id, disk.Path, Path.Combine("full", fileName), disk.VirtualSizeBytes, disk.PhysicalSizeBytes));
                backup.Files[disk.Id] = Path.Combine("full", fileName);
                backup.Hashes[disk.Id] = hash;
                backup.RctReferenceIds[disk.Id] = rctReferenceIds[disk.Id];
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
            return new BackupResult(backupId, chainId, BackupType.Full, BackupStatus.Completed, chainDirectory, SizeBytes: backup.SizeBytes);
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
        var chainDirectory = Directory.Exists(vmRoot)
            ? Directory.EnumerateDirectories(vmRoot, "chain-*").OrderBy(path => path).LastOrDefault()
            : null;
        if (chainDirectory is null)
        {
            return await RunFullBackupAsync(request, cancellationToken);
        }

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
            await EnsureRctReadyAsync(vm, consistentDisks, cancellationToken);
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
                var fileName = $"{ToSafeFileName(disk.Id)}.blocks";
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
            return new BackupResult(backupId, chain.ChainId, BackupType.Incremental, BackupStatus.Completed, chainDirectory, SizeBytes: backup.SizeBytes);
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

    private async Task EnsureRctReadyAsync(VirtualMachineInfo vm, IReadOnlyList<VirtualDiskInfo> disks, CancellationToken cancellationToken)
    {
        foreach (var disk in disks)
        {
            if (await _rct.IsAvailableAsync(vm, disk, cancellationToken))
            {
                continue;
            }

            var preparation = await _hyperV.PrepareForRctAsync(vm.Id, cancellationToken);
            if (!preparation.IsReady)
            {
                throw new InvalidOperationException(preparation.Message);
            }

            if (!await _rct.IsAvailableAsync(vm, disk, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"RCT is not available for disk '{disk.Id}' even after online Hyper-V reference point preparation. Check Hyper-V RCT support, pending checkpoints, and host event logs.");
            }
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }, cancellationToken);
    }

    private static async Task WriteChangedBlocksAsync(string sourcePath, string destinationPath, IReadOnlyList<ChangedRange> ranges, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var mountedDisk = await MountedVirtualDisk.OpenAsync(sourcePath, cancellationToken);
        await using var source = mountedDisk.OpenRead();
        await using var destination = File.Create(destinationPath);
        await using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(ranges.Count);
        var buffer = new byte[1024 * 1024];
        foreach (var range in ranges)
        {
            writer.Write(range.Offset);
            writer.Write(range.Length);
            source.Position = range.Offset;
            writer.Write(range.Length);
            var remaining = range.Length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Could not read changed range {range.Offset}:{range.Length} from {sourcePath}.");
                }

                writer.Write(buffer.AsSpan(0, read));
                remaining -= read;
            }
        }
    }

    private static string ToSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", value.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class MountedVirtualDisk : IAsyncDisposable
    {
        private readonly string _sourcePath;
        private readonly string _readPath;
        private readonly bool _mounted;

        private MountedVirtualDisk(string sourcePath, string readPath, bool mounted)
        {
            _sourcePath = sourcePath;
            _readPath = readPath;
            _mounted = mounted;
        }

        public static async Task<MountedVirtualDisk> OpenAsync(string sourcePath, CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(sourcePath);
            if (!extension.Equals(".vhdx", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".avhdx", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase))
            {
                return new MountedVirtualDisk(sourcePath, sourcePath, mounted: false);
            }

            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $path = '{{EscapePowerShell(sourcePath)}}'
                Mount-VHD -Path $path -ReadOnly -NoDriveLetter
                $disk = Get-DiskImage -ImagePath $path | Get-Disk
                [pscustomobject]@{ PhysicalDrive = '\\.\PhysicalDrive' + $disk.Number } | ConvertTo-Json -Compress
                """;

            try
            {
                var output = await RunPowerShellAsync(script, cancellationToken);
                using var document = JsonDocument.Parse(output);
                var physicalDrive = document.RootElement.GetProperty("PhysicalDrive").GetString()
                    ?? throw new InvalidOperationException($"Mount-VHD did not return a physical drive for {sourcePath}.");
                return new MountedVirtualDisk(sourcePath, physicalDrive, mounted: true);
            }
            catch
            {
                await TryDismountAsync(sourcePath, cancellationToken);
                throw;
            }
        }

        public FileStream OpenRead()
            => new(_readPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        public async ValueTask DisposeAsync()
        {
            if (_mounted)
            {
                await TryDismountAsync(_sourcePath, CancellationToken.None);
            }
        }

        private static async Task TryDismountAsync(string sourcePath, CancellationToken cancellationToken)
        {
            try
            {
                var script = $"Dismount-VHD -Path '{EscapePowerShell(sourcePath)}' -ErrorAction SilentlyContinue";
                await RunPowerShellAsync(script, cancellationToken);
            }
            catch
            {
                // Best effort cleanup. The caller records the original backup failure.
            }
        }

        private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Could not start powershell.exe.");

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(error.Trim());
            }

            return output.Trim();
        }

        private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    }
}
