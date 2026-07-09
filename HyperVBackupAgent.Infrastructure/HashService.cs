using System.Security.Cryptography;
using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class HashService : IHashService
{
    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
