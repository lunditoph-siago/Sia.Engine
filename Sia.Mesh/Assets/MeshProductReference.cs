using Sia.Asset;

namespace Sia.Engine.Mesh;

/// <summary>Typed inventory entry for an independently acquired product and its expected build key.</summary>
public sealed record MeshProductReference<TRecord> where TRecord : class, IAssetRecord
{
    public AssetId Id { get; }
    public string BuildKey { get; }
    public AssetRefer<TRecord> Reference => new AssetRefer<TRecord>.Id(Id);

    public MeshProductReference(AssetId id, string buildKey)
    {
        if (!id.IsValid) throw new ArgumentException("Product identity must be nonempty.", nameof(id));
        ArgumentNullException.ThrowIfNull(buildKey);
        if (buildKey.Length != 64 || buildKey.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Product build key must contain a SHA256 hexadecimal digest.", nameof(buildKey));
        Id = id;
        BuildKey = buildKey.ToUpperInvariant();
    }
}
