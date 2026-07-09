using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class RestoreEngine : IRestoreEngine
{
    private readonly IMetadataRepository _metadata;
    private readonly IHyperVService _hyperV;

    public RestoreEngine(IMetadataRepository metadata, IHyperVService hyperV)
    {
        _metadata = metadata;
        _hyperV = hyperV;
    }

    public async Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
    {
        var chain = await _metadata.LoadChainAsync(request.RestorePoint, cancellationToken);
        var full = chain.RestorePoints.First(point => point.Type == BackupType.Full);
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
            restoredDisks.Add(destination);
        }

        await _hyperV.CreateVmFromDisksAsync(request.NewName, restoredDisks, request.OverwriteExisting, cancellationToken);
    }
}
