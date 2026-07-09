using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class FileStorageProvider : IStorageProvider
{
    public Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetFullPath(path));
        return Task.CompletedTask;
    }

    public async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        await using var source = File.OpenRead(Path.GetFullPath(sourcePath));
        await using var destination = File.Create(fullDestination);
        await source.CopyToAsync(destination, cancellationToken);
    }

    public async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(Path.GetFullPath(path)));

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<Stream>(File.OpenRead(Path.GetFullPath(path)));
}
