using System.Collections.Immutable;

namespace Sia.Asset;

public sealed class AssetContent<TRecord> where TRecord : class, IAssetRecord
{
    public AssetId Id { get; }
    public ImmutableArray<AssetChunk> Chunks { get; }
    public long Length { get; }
    public long DecodedLength { get; }
    public string? Name { get; }
    public AssetRefer<TRecord> Reference => new AssetRefer<TRecord>.Id(Id);

    public AssetContent(AssetId id, IEnumerable<AssetChunk> chunks, string? name = null)
    {
        if (!id.IsValid) throw new ArgumentException("Asset identity must be nonempty.", nameof(id));
        ArgumentNullException.ThrowIfNull(chunks);
        Chunks = chunks.Take(AssetChunkManifest.MaximumChunks + 1).ToImmutableArray();
        if (Chunks.Length > AssetChunkManifest.MaximumChunks)
            throw new ArgumentException("Too many content chunks.", nameof(chunks));
        var lengths = new Dictionary<string, (int Stored, int Decoded, AssetChunkEncoding Encoding)>(StringComparer.Ordinal);
        long length = 0, decodedLength = 0;
        foreach (var chunk in Chunks) {
            ArgumentNullException.ThrowIfNull(chunk);
            var shape = (chunk.Length, chunk.DecodedLength, chunk.Encoding);
            if (lengths.TryGetValue(chunk.Id, out var previous) && previous != shape)
                throw new ArgumentException("Conflicting chunk length for the same content ID.", nameof(chunks));
            lengths[chunk.Id] = shape;
            length += chunk.Length;
            decodedLength += chunk.DecodedLength;
        }
        Id = id;
        Name = name;
        Length = length;
        DecodedLength = decodedLength;
    }
}
