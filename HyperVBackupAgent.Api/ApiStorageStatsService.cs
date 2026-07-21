using System.Runtime.InteropServices;

namespace HyperVBackupAgent.Api;

/// <summary>Reports total / free / used bytes for an absolute path's volume.
/// Works for local drives and UNC/SMB shares on Windows via GetDiskFreeSpaceEx,
/// and falls back to DriveInfo elsewhere (e.g. simulation on Linux).</summary>
public sealed class ApiStorageStatsService
{
    public StorageStats GetStats(string absolutePath)
    {
        var directory = absolutePath;
        // If the caller passes a file (rare here), resolve its containing directory.
        try { if (File.Exists(directory)) directory = Path.GetDirectoryName(directory) ?? directory; }
        catch { /* ignore — treat as directory */ }

        if (TryGetSpace(directory, out var total, out var free) && total > 0)
        {
            return new StorageStats(total, free, Math.Max(0, total - free));
        }

        return new StorageStats(0, 0, 0); // unknown / unavailable
    }

    private static bool TryGetSpace(string directory, out long totalBytes, out long freeBytes)
    {
        totalBytes = 0;
        freeBytes = 0;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // GetDiskFreeSpaceEx handles both drive roots (C:\) and UNC paths (\\server\share).
            if (GetDiskFreeSpaceEx(directory, out var freeAvailable, out var total, out _))
            {
                totalBytes = (long)total;
                freeBytes = (long)freeAvailable;
                return totalBytes >= 0 && freeBytes >= 0;
            }
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(directory);
            if (string.IsNullOrEmpty(root)) return false;
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Unknown || !drive.IsReady) return false;
            totalBytes = drive.TotalSize;
            freeBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
}

/// <summary>Disk capacity for a path. Zero values mean "unavailable".</summary>
public sealed record StorageStats(long TotalBytes, long FreeBytes, long UsedBytes);
