namespace Sia.Asset;

/// <summary>A wire-byte region in one independently authenticated Raw storage chunk.</summary>
public readonly record struct AssetChunkSlice
{
    public AssetChunk Pack { get; }
    public int Offset { get; }

    public AssetChunkSlice(AssetChunk pack, int offset)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (pack.Encoding != AssetChunkEncoding.Raw || pack.Dependencies.Length != 0 || offset >= pack.Length)
            throw new ArgumentException("A storage slice requires a Raw pack and an in-bounds offset.", nameof(pack));
        Pack = pack; Offset = offset;
    }
}
