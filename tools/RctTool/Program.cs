using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("enable" or "info"))
        {
            Console.Error.WriteLine("Usage: RctTool enable <vhdx-path> | RctTool info <vhdx-path>");
            return 2;
        }

        var command = args[0];
        var path = Path.GetFullPath(args[1]);

        if (command == "enable")
        {
            EnableChangeTracking(path);
        }

        using var handle = OpenDisk(path, VirtualDiskAccessMask.GetInfo);
        var state = ReadChangeTrackingState(handle, path);
        Console.WriteLine($"enabled={state.Enabled}");
        Console.WriteLine($"mostRecentChangeTrackingId={state.MostRecentChangeTrackingId}");
        return state.Enabled ? 0 : 1;
    }

    private static SafeFileHandle OpenDisk(string path, VirtualDiskAccessMask accessMask)
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

        var error = OpenVirtualDisk(ref storageType, path, accessMask, OpenVirtualDiskFlag.None, ref parameters, out var handle);
        if (error != 0)
        {
            throw CreateWin32Exception(error, $"OpenVirtualDisk failed for '{path}'");
        }

        return handle;
    }

    private static void EnableChangeTracking(string path)
    {
        using var handle = OpenDisk(path, VirtualDiskAccessMask.All);
        var info = new SetVirtualDiskInfoChangeTracking
        {
            Version = 6,
            ChangeTrackingEnabled = 1
        };

        var error = SetVirtualDiskInformation(handle, ref info);
        if (error != 0)
        {
            throw CreateWin32Exception(error, $"SetVirtualDiskInformation CHANGE_TRACKING_STATE failed for '{path}'");
        }
    }

    private static ChangeTrackingState ReadChangeTrackingState(SafeFileHandle handle, string path)
    {
        var size = 4096u;
        var used = 0u;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            Span<byte> empty = stackalloc byte[(int)size];
            Marshal.Copy(empty.ToArray(), 0, buffer, (int)size);
            Marshal.WriteInt32(buffer, 15);

            var error = GetVirtualDiskInformation(handle, ref size, buffer, ref used);
            if (error != 0)
            {
                throw CreateWin32Exception(error, $"GetVirtualDiskInformation CHANGE_TRACKING_STATE failed for '{path}'");
            }

            var enabled = Marshal.ReadInt32(buffer, 8) != 0;
            var id = Marshal.PtrToStringUni(IntPtr.Add(buffer, 16)) ?? string.Empty;
            return new ChangeTrackingState(enabled, id);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Win32Exception CreateWin32Exception(uint error, string operation)
    {
        var inner = new Win32Exception((int)error);
        return new Win32Exception((int)error, $"{operation}. Win32Error={error}: {inner.Message}");
    }

    private sealed record ChangeTrackingState(bool Enabled, string MostRecentChangeTrackingId);

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [Flags]
    private enum VirtualDiskAccessMask : uint
    {
        GetInfo = 0x00080000,
        MetaOps = 0x00200000,
        All = 0x003f0000
    }

    private enum OpenVirtualDiskFlag : uint
    {
        None = 0
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

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        VirtualDiskAccessMask virtualDiskAccessMask,
        OpenVirtualDiskFlag flags,
        ref OpenVirtualDiskParameters parameters,
        out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    private static extern uint SetVirtualDiskInformation(
        SafeFileHandle virtualDiskHandle,
        ref SetVirtualDiskInfoChangeTracking virtualDiskInfo);

    [DllImport("virtdisk.dll")]
    private static extern uint GetVirtualDiskInformation(
        SafeFileHandle virtualDiskHandle,
        ref uint virtualDiskInfoSize,
        IntPtr virtualDiskInfo,
        ref uint sizeUsed);
}
