using HyperVBackupAgent.Core;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

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
    private const uint ChangeTrackingStateVersion = 15;
    private const uint ErrorSuccess = 0;
    private readonly IPowerShellRunner _powerShell;

    public NativeHyperVRctService(IPowerShellRunner powerShell)
    {
        _powerShell = powerShell;
    }

    public async Task<bool> IsAvailableAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, CancellationToken cancellationToken = default)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(await GetCurrentChangeIdAsync(vm, disk, cancellationToken));
        }
        catch
        {
            return false;
        }
    }

    public Task<string> GetCurrentChangeIdAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(disk.Path))
        {
            throw new InvalidOperationException($"Disk {disk.Id} does not have a VHDX path.");
        }

        using var handle = OpenDiskForInfo(disk.Path);
        var changeId = ReadMostRecentChangeTrackingId(handle, disk.Path, throwWhenDisabled: false);
        if (string.IsNullOrWhiteSpace(changeId))
        {
            EnableChangeTracking(disk.Path);
            using var refreshedHandle = OpenDiskForInfo(disk.Path);
            changeId = ReadMostRecentChangeTrackingId(refreshedHandle, disk.Path, throwWhenDisabled: true);
        }

        if (string.IsNullOrWhiteSpace(changeId))
        {
            throw new InvalidOperationException($"RCT is not enabled or no change tracking id is available for disk '{disk.Path}'.");
        }

        return Task.FromResult(changeId);
    }

    public async Task<RctDiskState> GetChangedRangesAsync(VirtualMachineInfo vm, VirtualDiskInfo disk, string previousChangeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(previousChangeId))
        {
            throw new InvalidOperationException("A previous RCT change id is required for incremental backup. Run a full backup first.");
        }

        var endChangeId = await GetCurrentChangeIdAsync(vm, disk, cancellationToken);
        IReadOnlyList<ChangedRange> ranges;
        try
        {
            ranges = QueryChangedRangesViaVirtdisk(disk, previousChangeId, cancellationToken);
        }
        catch (Win32Exception)
        {
            ranges = await QueryChangedRangesViaWmiAsync(disk, previousChangeId, cancellationToken);
        }

        if (ranges.Count == 0)
        {
            ranges = await QueryChangedRangesViaWmiAsync(disk, previousChangeId, cancellationToken);
        }

        return new RctDiskState(disk.Id, previousChangeId, endChangeId, ranges);
    }

    private static IReadOnlyList<ChangedRange> QueryChangedRangesViaVirtdisk(VirtualDiskInfo disk, string previousChangeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(disk.Path))
        {
            throw new InvalidOperationException($"Disk {disk.Id} does not have a VHDX path.");
        }

        using var handle = OpenDiskForInfo(disk.Path);
        var ranges = new List<ChangedRange>();
        var totalLength = (ulong)Math.Max(disk.VirtualSizeBytes, disk.PhysicalSizeBytes);
        var offset = 0UL;
        const ulong chunkLength = 1024UL * 1024UL * 1024UL;
        const uint maxRangesPerCall = 16384;

        while (offset < totalLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var byteLength = Math.Min(chunkLength, totalLength - offset);
            var rangeCount = maxRangesPerCall;
            var processedLength = 0UL;
            var rangeSize = Marshal.SizeOf<QueryChangesVirtualDiskRange>();
            var bufferSize = checked((int)(rangeCount * (uint)rangeSize));
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var error = QueryChangesVirtualDisk(
                    handle,
                    previousChangeId,
                    offset,
                    byteLength,
                    QueryChangesVirtualDiskFlag.None,
                    buffer,
                    ref rangeCount,
                    out processedLength);

                if (error != ErrorSuccess)
                {
                    throw CreateWin32Exception(error, $"QueryChangesVirtualDisk failed for '{disk.Path}' at offset {offset} with change id '{previousChangeId}'");
                }

                for (var i = 0; i < rangeCount; i++)
                {
                    var current = IntPtr.Add(buffer, i * rangeSize);
                    var range = Marshal.PtrToStructure<QueryChangesVirtualDiskRange>(current);
                    if (range.ByteLength > 0)
                    {
                        ranges.Add(new ChangedRange(checked((long)range.ByteOffset), checked((long)range.ByteLength)));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            offset += processedLength > 0 ? processedLength : byteLength;
        }

        return MergeRanges(ranges);
    }

    private async Task<IReadOnlyList<ChangedRange>> QueryChangedRangesViaWmiAsync(VirtualDiskInfo disk, string previousChangeId, CancellationToken cancellationToken)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $path = '{{Escape(disk.Path)}}'
            $limitId = '{{Escape(previousChangeId)}}'
            $totalLength = [UInt64]{{Math.Max(disk.VirtualSizeBytes, disk.PhysicalSizeBytes)}}
            $chunkSize = [UInt64](1024 * 1024 * 1024)
            $service = Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ImageManagementService
            $ranges = @()
            $offset = [UInt64]0
            while ($offset -lt $totalLength) {
              $length = [UInt64][Math]::Min($chunkSize, $totalLength - $offset)
              if ($length -eq 0) { break }
              $result = Invoke-CimMethod -InputObject $service -MethodName GetVirtualDiskChanges -Arguments @{
                Path = $path
                LimitId = $limitId
                TargetSnapshotId = ''
                ByteOffset = $offset
                ByteLength = $length
              }
              if ($result.ReturnValue -ne 0) {
                throw "GetVirtualDiskChanges failed with return value $($result.ReturnValue) for $path at offset $offset."
              }
              for ($i = 0; $i -lt $result.ChangedByteOffsets.Count; $i++) {
                $ranges += [pscustomobject]@{
                  Offset = [Int64]$result.ChangedByteOffsets[$i]
                  Length = [Int64]$result.ChangedByteLengths[$i]
                }
              }
              if ($result.ProcessedByteLength -gt 0) {
                $offset += [UInt64]$result.ProcessedByteLength
              } else {
                $offset += $length
              }
            }
            $ranges | ConvertTo-Json -Depth 4
            """;

        var result = await _powerShell.RunAsync(script, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Hyper-V RCT WMI query failed: {result.StandardError.Trim()}");
        }

        return MergeRanges(DeserializeRanges(result.StandardOutput));
    }

    private static IReadOnlyList<ChangedRange> MergeRanges(IReadOnlyList<ChangedRange> ranges)
    {
        if (ranges.Count <= 1)
        {
            return ranges;
        }

        var ordered = ranges
            .Where(range => range.Length > 0)
            .OrderBy(range => range.Offset)
            .ThenBy(range => range.Length)
            .ToArray();

        var merged = new List<ChangedRange>();
        foreach (var range in ordered)
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            var previous = merged[^1];
            var previousEnd = previous.Offset + previous.Length;
            var currentEnd = range.Offset + range.Length;
            if (range.Offset <= previousEnd)
            {
                merged[^1] = previous with { Length = Math.Max(previousEnd, currentEnd) - previous.Offset };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private static IReadOnlyList<ChangedRange> DeserializeRanges(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (json.TrimStart().StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<ChangedRange>>(json, options) ?? [];
        }

        var single = JsonSerializer.Deserialize<ChangedRange>(json, options);
        return single is null ? [] : [single];
    }

    private static SafeFileHandle OpenDiskForInfo(string path)
    {
        var storageType = new VirtualStorageType
        {
            DeviceId = 0,
            VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B")
        };

        var parameters = new OpenVirtualDiskParameters
        {
            Version = 1,
            RWDepth = 1
        };

        var error = OpenVirtualDisk(
            ref storageType,
            path,
            VirtualDiskAccessMask.GetInfo,
            OpenVirtualDiskFlag.None,
            ref parameters,
            out var handle);

        if (error != ErrorSuccess)
        {
            throw CreateWin32Exception(error, $"OpenVirtualDisk failed for '{path}'.");
        }

        return handle;
    }

    private static void EnableChangeTracking(string path)
    {
        var storageType = new VirtualStorageType
        {
            DeviceId = 0,
            VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B")
        };

        var parameters = new OpenVirtualDiskParameters
        {
            Version = 1,
            RWDepth = 1
        };

        var error = OpenVirtualDisk(
            ref storageType,
            path,
            VirtualDiskAccessMask.All,
            OpenVirtualDiskFlag.None,
            ref parameters,
            out var handle);

        if (error != ErrorSuccess)
        {
            throw CreateWin32Exception(error, $"OpenVirtualDisk METAOPS failed for '{path}'.");
        }

        using (handle)
        {
            var info = new SetVirtualDiskInfoChangeTracking
            {
                Version = 6,
                ChangeTrackingEnabled = 1
            };

            error = SetVirtualDiskInformation(handle, ref info);
            if (error != ErrorSuccess)
            {
                throw CreateWin32Exception(error, $"SetVirtualDiskInformation CHANGE_TRACKING_STATE failed for '{path}'.");
            }
        }
    }

    private static string ReadMostRecentChangeTrackingId(SafeFileHandle handle, string path, bool throwWhenDisabled)
    {
        var size = 4096u;
        var used = 0u;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            Span<byte> empty = stackalloc byte[(int)size];
            Marshal.Copy(empty.ToArray(), 0, buffer, (int)size);
            Marshal.WriteInt32(buffer, (int)ChangeTrackingStateVersion);

            var error = GetVirtualDiskInformation(handle, ref size, buffer, ref used);
            if (error != ErrorSuccess)
            {
                throw new Win32Exception((int)error, $"GetVirtualDiskInformation CHANGE_TRACKING_STATE failed for '{path}'.");
            }

            var enabled = Marshal.ReadInt32(buffer, 8) != 0;
            if (!enabled)
            {
                if (!throwWhenDisabled)
                {
                    return string.Empty;
                }

                throw new InvalidOperationException($"RCT is disabled for disk '{path}'.");
            }

            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 16)) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static Win32Exception CreateWin32Exception(uint error, string operation)
    {
        var inner = new Win32Exception((int)error);
        return new Win32Exception((int)error, $"{operation} Win32Error={error}: {inner.Message}");
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        VirtualDiskAccessMask virtualDiskAccessMask,
        OpenVirtualDiskFlag flags,
        ref OpenVirtualDiskParameters parameters,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern uint GetVirtualDiskInformation(
        SafeFileHandle virtualDiskHandle,
        ref uint virtualDiskInfoSize,
        IntPtr virtualDiskInfo,
        ref uint sizeUsed);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryChangesVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        string changeTrackingId,
        ulong byteOffset,
        ulong byteLength,
        QueryChangesVirtualDiskFlag flags,
        IntPtr ranges,
        ref uint rangeCount,
        out ulong processedLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [Flags]
    private enum VirtualDiskAccessMask : uint
    {
        MetaOps = 0x00200000,
        GetInfo = 0x00080000,
        All = 0x003f0000
    }

    private enum OpenVirtualDiskFlag : uint
    {
        None = 0
    }

    private enum QueryChangesVirtualDiskFlag : uint
    {
        None = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryChangesVirtualDiskRange
    {
        public ulong ByteOffset;
        public ulong ByteLength;
        public ulong Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetVirtualDiskInfoChangeTracking
    {
        public uint Version;
        public uint Padding;
        public int ChangeTrackingEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenVirtualDiskParameters
    {
        public uint Version;
        public uint RWDepth;
    }

    [DllImport("virtdisk.dll")]
    private static extern uint SetVirtualDiskInformation(
        SafeFileHandle virtualDiskHandle,
        ref SetVirtualDiskInfoChangeTracking virtualDiskInfo);
}
