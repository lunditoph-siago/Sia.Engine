using System.Collections.Immutable;

namespace Sia.Asset;

public sealed class AssetContent<TRecord> where TRecord : class, IAssetRecord
{
    public AssetId Id { get; }
    public ImmutableArray<AssetChunk> Chunks { get; }
    public long Length { get; }
    public string? Name { get; }
    public AssetRefer<TRecord> Reference => new AssetRefer<TRecord>.Id(Id);

    public AssetContent(AssetId id, IEnumerable<AssetChunk> chunks, string? name = null)
    {
        if (!id.IsValid) throw new ArgumentException("Asset identity must be nonempty.", nameof(id));
        ArgumentNullException.ThrowIfNull(chunks);
        Chunks = chunks.Take(AssetChunkManifest.MaximumChunks + 1).ToImmutableArray();
        if (Chunks.Length > AssetChunkManifest.MaximumChunks)
            throw new ArgumentException("Too many content chunks.", nameof(chunks));
        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        long length = 0;
        foreach (var chunk in Chunks) {
            ArgumentNullException.ThrowIfNull(chunk);
            if (lengths.TryGetValue(chunk.Id, out var previous) && previous != chunk.Length)
                throw new ArgumentException("Conflicting chunk length for the same content ID.", nameof(chunks));
            lengths[chunk.Id] = chunk.Length;
            length += chunk.Length;
        }
        Id = id;
        Name = name;
        Length = length;
    }
}
