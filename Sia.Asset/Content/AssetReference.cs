namespace Sia.Asset;

/// <summary>A typed reference with the payload bounds required before IO admission.</summary>
public readonly record struct AssetReference<T>
{
    public AssetId Id { get; }
    public int EncodedBytes { get; }
    public int DecodedBytes { get; }

    public AssetReference(AssetId id, int encodedBytes, int decodedBytes)
    {
        if (id == default) throw new ArgumentException("An initialized asset ID is required.", nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegative(encodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(decodedBytes);
        Id = id;
        EncodedBytes = encodedBytes;
        DecodedBytes = decodedBytes;
    }
}
