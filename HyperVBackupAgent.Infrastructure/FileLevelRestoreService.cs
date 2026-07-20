using System.Text.Json;
using HyperVBackupAgent.Core;
using Microsoft.Extensions.Configuration;

namespace HyperVBackupAgent.Infrastructure;

public sealed class FileLevelRestoreService : IFileLevelRestoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RestoreMaterializer _materializer;
    private readonly IPowerShellRunner _powerShell;
    private readonly string _root;
    private readonly int _defaultTtlMinutes;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public FileLevelRestoreService(RestoreMaterializer materializer, IPowerShellRunner powerShell, IConfiguration configuration)
    {
        _materializer = materializer;
        _powerShell = powerShell;
        _defaultTtlMinutes = Math.Clamp(
            int.TryParse(configuration["HyperVBackupAgent:FileLevelRestore:TtlMinutes"], out var configuredTtl) ? configuredTtl : 60,
            5,
            24 * 60);
        _root = configuration["HyperVBackupAgent:FileLevelRestore:TemporaryRoot"]
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyperVBackupAgent", "flr")
                : Path.Combine(Path.GetTempPath(), "HyperVBackupAgent", "flr"));
        _root = Path.GetFullPath(_root);
    }

    public async Task<FileLevelRestoreSession> CreateSessionAsync(FileLevelRestoreRequest request, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("File-level restore requires Windows and Hyper-V Mount-VHD.");
        }

        if (string.IsNullOrWhiteSpace(request.RestorePoint))
        {
            throw new ArgumentException("RestorePoint is required.");
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_root, sessionId);
        var ttl = Math.Clamp(request.TtlMinutes ?? _defaultTtlMinutes, 5, 24 * 60);
        var createdAt = DateTimeOffset.UtcNow;
        var state = new SessionState(sessionId, directory, createdAt, createdAt.AddMinutes(ttl), [], []);
        try
        {
            Directory.CreateDirectory(directory);
            var materialized = await _materializer.MaterializeAsync(
                new RestoreRequest(request.RestorePoint, directory, "flr", OverwriteExisting: true, request.TargetBackupId, CreateVm: false),
                cancellationToken);
            state = state with { DiskPaths = materialized.DiskPaths.ToArray() };
            var volumes = await MountAndDiscoverVolumesAsync(state.DiskPaths, cancellationToken);
            if (volumes.Count == 0)
            {
                throw new InvalidOperationException("No Windows volumes with a mount path were found in the restored VHDX files.");
            }

            state = state with { Volumes = volumes };
            await PersistAsync(state, cancellationToken);
            lock (_gate)
            {
                _sessions.Add(sessionId, state);
            }

            return ToPublic(state);
        }
        catch
        {
            await DismountAsync(state.DiskPaths, CancellationToken.None);
            DeleteDirectory(directory);
            throw;
        }
    }

    public FileLevelRestoreSession? GetSession(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var state) && state.ExpiresAt > DateTimeOffset.UtcNow
                ? ToPublic(state)
                : null;
        }
    }

    public IReadOnlyList<FileLevelRestoreEntry> ListEntries(string sessionId, string volumeId, string? relativePath)
    {
        var directory = ResolvePath(sessionId, volumeId, relativePath, mustBeFile: false);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Directory was not found in the mounted restore volume: {relativePath}");
        }

        return Directory.EnumerateFileSystemEntries(directory)
            .Select(path =>
            {
                var attributes = File.GetAttributes(path);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var info = isDirectory ? new DirectoryInfo(path) as FileSystemInfo : new FileInfo(path);
                return new FileLevelRestoreEntry(
                    info.Name,
                    Path.GetRelativePath(GetVolume(sessionId, volumeId).MountPath, path),
                    isDirectory,
                    isDirectory ? null : ((FileInfo)info).Length,
                    info.LastWriteTimeUtc);
            })
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string GetFilePath(string sessionId, string volumeId, string relativePath)
    {
        var path = ResolvePath(sessionId, volumeId, relativePath, mustBeFile: true);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("File was not found in the mounted restore volume.");
        }

        return path;
    }

    public async Task<bool> CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        SessionState? state;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out state))
            {
                return false;
            }
        }

        await DismountAsync(state.DiskPaths, cancellationToken);
        DeleteDirectory(state.Directory);
        return true;
    }

    public async Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        string[] expired;
        lock (_gate)
        {
            expired = _sessions.Values.Where(state => state.ExpiresAt <= DateTimeOffset.UtcNow).Select(state => state.SessionId).ToArray();
        }

        foreach (var sessionId in expired)
        {
            await CloseSessionAsync(sessionId, cancellationToken);
        }
    }

    public async Task CleanupOrphanedSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            try
            {
                var sessionFile = Path.Combine(directory, "session.json");
                if (File.Exists(sessionFile))
                {
                    var state = JsonSerializer.Deserialize<SessionState>(await File.ReadAllTextAsync(sessionFile, cancellationToken), JsonOptions);
                    if (state is not null)
                    {
                        await DismountAsync(state.DiskPaths, cancellationToken);
                    }
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }
    }

    private async Task<IReadOnlyList<FileLevelRestoreVolume>> MountAndDiscoverVolumesAsync(IReadOnlyList<string> diskPaths, CancellationToken cancellationToken)
    {
        var paths = string.Join(",", diskPaths.Select(path => $"'{Escape(path)}'"));
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $paths = @({{paths}})
            $result = foreach ($path in $paths) {
              $disk = Mount-VHD -Path $path -ReadOnly -Passthru
              Get-Partition -DiskNumber $disk.DiskNumber | ForEach-Object {
                $partition = $_
                $volume = $partition | Get-Volume -ErrorAction SilentlyContinue
                if ($volume -and $volume.DriveLetter) {
                  [pscustomobject]@{
                    DiskPath = $path
                    PartitionNumber = $partition.PartitionNumber
                    MountPath = "$($volume.DriveLetter):\\"
                    Label = $volume.FileSystemLabel
                    FileSystem = $volume.FileSystem
                    SizeBytes = [int64]$partition.Size
                  }
                }
              }
            }
            @($result) | ConvertTo-Json -Compress
            """;
        var result = await _powerShell.RunAsync(script, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not mount restored VHDX read-only: {result.StandardError.Trim()}");
        }

        var discovered = JsonSerializer.Deserialize<List<DiscoveredVolume>>(NormalizeJsonArray(result.StandardOutput), JsonOptions) ?? [];
        return discovered
            .Where(volume => !string.IsNullOrWhiteSpace(volume.MountPath))
            .Select(volume => new FileLevelRestoreVolume(
                $"{Path.GetFileNameWithoutExtension(volume.DiskPath)}-p{volume.PartitionNumber}",
                Path.GetFullPath(volume.MountPath), volume.Label, volume.FileSystem, volume.SizeBytes))
            .DistinctBy(volume => volume.VolumeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task DismountAsync(IReadOnlyList<string> diskPaths, CancellationToken cancellationToken)
    {
        foreach (var path in diskPaths.Reverse())
        {
            var result = await _powerShell.RunAsync($"Dismount-VHD -Path '{Escape(path)}' -ErrorAction SilentlyContinue", cancellationToken);
            _ = result;
        }
    }

    private string ResolvePath(string sessionId, string volumeId, string? relativePath, bool mustBeFile)
    {
        var volume = GetVolume(sessionId, volumeId);
        var candidate = relativePath ?? string.Empty;
        if (Path.IsPathFullyQualified(candidate) || candidate.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
        {
            throw new ArgumentException("Path must be relative to the selected restore volume and cannot contain parent traversal.");
        }

        var root = Path.GetFullPath(volume.MountPath);
        var path = Path.GetFullPath(Path.Combine(root, candidate));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root, comparison))
        {
            throw new ArgumentException("Path is outside the selected restore volume.");
        }

        if (mustBeFile && Directory.Exists(path))
        {
            throw new ArgumentException("Path must identify a file.");
        }

        return path;
    }

    private FileLevelRestoreVolume GetVolume(string sessionId, string volumeId)
    {
        SessionState state;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out state!) || state.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("File-level restore session was not found or has expired.");
            }
        }

        return state.Volumes.FirstOrDefault(volume => string.Equals(volume.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Restore volume was not found in this session.");
    }

    private static string NormalizeJsonArray(string json)
    {
        var trimmed = json.Trim();
        return string.IsNullOrEmpty(trimmed) ? "[]" : trimmed.StartsWith('[') ? trimmed : $"[{trimmed}]";
    }

    private static FileLevelRestoreSession ToPublic(SessionState state) => new(state.SessionId, state.CreatedAt, state.ExpiresAt, state.Volumes);
    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task PersistAsync(SessionState state, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(Path.Combine(state.Directory, "session.json"), JsonSerializer.Serialize(state, JsonOptions), cancellationToken);

    private sealed record SessionState(
        string SessionId,
        string Directory,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        IReadOnlyList<string> DiskPaths,
        IReadOnlyList<FileLevelRestoreVolume> Volumes);

    private sealed record DiscoveredVolume(string DiskPath, int PartitionNumber, string MountPath, string? Label, string? FileSystem, long SizeBytes);
}
