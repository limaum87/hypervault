using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVBackupAgent.Core;

namespace HyperVBackupAgent.Infrastructure;

public sealed class JsonMetadataRepository : IMetadataRepository
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveChainAsync(string chainDirectory, BackupChainMetadata chain, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetFullPath(chainDirectory));
        var path = Path.Combine(chainDirectory, "chain.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, chain, Options, cancellationToken);
    }

    public async Task<BackupChainMetadata> LoadChainAsync(string chainDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetFullPath(chainDirectory), "chain.json");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BackupChainMetadata>(stream, Options, cancellationToken)
            ?? throw new InvalidOperationException($"Invalid metadata file: {path}");
    }
}
