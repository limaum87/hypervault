using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class RestoreEngine : IRestoreEngine
{
    private readonly IHyperVService _hyperV;
    private readonly RestoreMaterializer _materializer;

    public RestoreEngine(IHyperVService hyperV, RestoreMaterializer materializer)
    {
        _hyperV = hyperV;
        _materializer = materializer;
    }

    public async Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
    {
        var materialized = await _materializer.MaterializeAsync(request, cancellationToken);
        // CreateVm=false => "disk only / test" mode: mount the chain and materialize the
        // restored VHD/X files into Destination, but do not create a Hyper-V VM. The disks
        // are left in place, ready to be attached/mounted manually for inspection.
        if (request.CreateVm)
        {
            await _hyperV.CreateVmFromDisksAsync(request.NewName, materialized.DiskPaths, request.OverwriteExisting, cancellationToken);
        }
    }
}
