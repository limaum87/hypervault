using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed record MaterializedRestore(IReadOnlyList<string> DiskPaths, string Directory);

public sealed class RestoreMaterializer
{
    private readonly IMetadataRepository _metadata;

    public RestoreMaterializer(IMetadataRepository metadata)
    {
        _metadata = metadata;
    }

    public async Task<MaterializedRestore> MaterializeAsync(RestoreRequest request, CancellationToken cancellationToken = default)
    {
        var chain = await _metadata.LoadChainAsync(request.RestorePoint, cancellationToken);
        var restorePoints = SelectRestorePoints(chain, request.TargetBackupId);
        var full = restorePoints.First(point => point.Type == BackupType.Full);
        var increments = restorePoints
            .Where(point => point.Type == BackupType.Incremental)
            .OrderBy(point => point.CreatedAt)
            .ToArray();
        var restoreDirectory = Path.GetFullPath(request.Destination);
        Directory.CreateDirectory(restoreDirectory);

        var restoredDisks = new List<string>();
        foreach (var disk in full.Disks)
        {
            var source = Path.Combine(request.RestorePoint, disk.BackupFile);
            var destination = Path.Combine(restoreDirectory, $"{request.NewName}-{Path.GetFileName(disk.SourcePath)}");
            if (File.Exists(destination) && !request.OverwriteExisting)
            {
                throw new InvalidOperationException($"Restore target exists: {destination}");
            }

            File.Copy(source, destination, overwrite: request.OverwriteExisting);
            foreach (var increment in increments)
            {
                if (!increment.Files.TryGetValue(disk.DiskId, out var blockFile))
                {
                    throw new InvalidOperationException($"Increment {increment.BackupId} is missing block data for disk {disk.DiskId}.");
                }

                await ApplyBlockFileAsync(destination, Path.Combine(request.RestorePoint, blockFile), cancellationToken);
            }

            restoredDisks.Add(destination);
        }

        return new MaterializedRestore(restoredDisks, restoreDirectory);
    }

    private static IReadOnlyList<BackupMetadata> SelectRestorePoints(BackupChainMetadata chain, string? targetBackupId)
    {
        if (chain.RestorePoints.Count == 0)
        {
            throw new InvalidOperationException($"Chain {chain.ChainId} has no restore points.");
        }

        var ordered = chain.RestorePoints.OrderBy(point => point.CreatedAt).ToArray();
        if (string.IsNullOrWhiteSpace(targetBackupId))
        {
            return ordered;
        }

        var targetIndex = Array.FindIndex(ordered, point => string.Equals(point.BackupId, targetBackupId, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
        {
            throw new InvalidOperationException($"Restore point '{targetBackupId}' was not found in chain {chain.ChainId}.");
        }

        return ordered.Take(targetIndex + 1).ToArray();
    }

    private static async Task ApplyBlockFileAsync(string diskPath, string blockFilePath, CancellationToken cancellationToken)
    {
        await using var disk = File.Open(diskPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var blockFile = File.OpenRead(blockFilePath);
        using var reader = new BinaryReader(blockFile, System.Text.Encoding.UTF8, leaveOpen: true);

        var rangeCount = reader.ReadInt32();
        var buffer = new byte[1024 * 1024];
        for (var index = 0; index < rangeCount; index++)
        {
            var offset = reader.ReadInt64();
            var expectedLength = reader.ReadInt64();
            var actualLength = reader.ReadInt64();

            if (actualLength < 0 || actualLength > expectedLength)
            {
                throw new InvalidDataException($"Invalid block length in {blockFilePath} at range {index}.");
            }

            disk.Position = offset;
            var remaining = actualLength;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await blockFile.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Unexpected end of block file {blockFilePath}.");
                }

                await disk.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
    }
}
