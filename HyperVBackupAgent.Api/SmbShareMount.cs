using System.Diagnostics;
using System.Runtime.InteropServices;
using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Api;

/// <summary>Establishes (and releases) an authenticated SMB connection to a UNC
/// share using the Windows `net use` command, so the agent can back up to a
/// network share that requires explicit credentials.
///
/// On non-Windows hosts or when no credentials are supplied, Mount is a no-op
/// and the host's own filesystem access applies (useful for local paths and for
/// the Linux simulation environment). The connection is torn down on Dispose.</summary>
public sealed class SmbShareMount : IAsyncDisposable
{
    private readonly string? _remoteName; // \\server\share ; null => nothing was mounted

    private SmbShareMount(string? remoteName) => _remoteName = remoteName;

    /// <summary>True when this instance actually established a connection.</summary>
    public bool IsActive => _remoteName is not null;

    /// <summary>Mounts the share when <paramref name="creds"/> are present and <paramref name="path"/>
    /// is a UNC path. Returns a no-op mount otherwise. Throws on authentication failure.</summary>
    public static SmbShareMount Mount(string path, SmbCredentials? creds)
    {
        if (creds is null || !creds.HasCredentials || !IsUncPath(path))
            return new SmbShareMount(null);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new InvalidOperationException(
                "SMB credentials are only supported when the agent runs on Windows.");

        var remoteName = ExtractShareRoot(path);
        var mount = new SmbShareMount(remoteName);
        mount.Establish(creds);
        return mount;
    }

    private void Establish(SmbCredentials creds)
    {
        // Drop any pre-existing mapping for this share to avoid Windows error 1219
        // ("multiple connections to a server or shared resource by the same user").
        RunNet($"use \"{_remoteName}\" /delete /y", throwOnError: false);

        var user = string.IsNullOrWhiteSpace(creds.Domain)
            ? creds.Username!
            : $"{creds.Domain}\\{creds.Username}";

        // NOTE: the password is passed as a `net use` argument. This is the
        // simplest reliable non-interactive form; a future hardening could use
        // WNetAddConnection2 (P/Invoke) to avoid exposing it in the process list.
        var escapedPassword = (creds.Password ?? string.Empty).Replace("\"", "\"\"");
        var args = $"use \"{_remoteName}\" \"{escapedPassword}\" /USER:\"{user}\" /PERSISTENT:NO";
        RunNet(args, throwOnError: true);
    }

    public ValueTask DisposeAsync()
    {
        if (_remoteName is not null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { RunNet($"use \"{_remoteName}\" /delete /y", throwOnError: false); }
            catch { /* best-effort cleanup; never throw from Dispose */ }
        }
        return ValueTask.CompletedTask;
    }

    private static void RunNet(string arguments, bool throwOnError)
    {
        using var process = Process.Start(new ProcessStartInfo("net", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("Failed to start 'net'.");

        // Read stderr before WaitForExit to avoid deadlocks on large output.
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(); } catch { /* ignore */ }
            if (throwOnError) throw new InvalidOperationException("'net use' timed out.");
            return;
        }

        if (throwOnError && process.ExitCode != 0)
        {
            var msg = string.IsNullOrWhiteSpace(stderr) ? $"exit code {process.ExitCode}" : stderr.Trim();
            throw new InvalidOperationException($"SMB authentication failed: {msg}");
        }
    }

    internal static bool IsUncPath(string? path)
        => !string.IsNullOrEmpty(path) && path!.Length >= 2 && path[0] == '\\' && path[1] == '\\';

    /// <summary>\\server\share[\sub...\file] -> \\server\share</summary>
    internal static string ExtractShareRoot(string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException($"Cannot determine SMB share root from '{path}'.");
        return $@"\\{parts[0]}\{parts[1]}";
    }
}
