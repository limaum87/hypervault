using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class SimulatedRctService : IRctService
{
    public Task<bool> IsAvailableAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<string> GetCurrentChangeIdAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, CancellationToken cancellationToken = default)
        => Task.FromResult($"{disk.Id}:{disk.PhysicalSizeBytes}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

    public Task<RctDiskState> GetChangedRangesAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, string previousChangeId, CancellationToken cancellationToken = default)
    {
        var length = Math.Min(disk.PhysicalSizeBytes, 1024 * 1024);
        IReadOnlyList<ChangedRange> ranges = length > 0 ? [new ChangedRange(0, length)] : [];
        return Task.FromResult(new RctDiskState(disk.Id, previousChangeId, $"{disk.Id}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", ranges));
    }
}

public sealed class NativeHyperVRctService : IRctService
{
    public Task<bool> IsAvailableAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<string> GetCurrentChangeIdAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Native Hyper-V RCT access is isolated here for a later WMI/CIM/native implementation.");

    public Task<RctDiskState> GetChangedRangesAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, string previousChangeId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Native Hyper-V RCT access is isolated here for a later WMI/CIM/native implementation.");
}
