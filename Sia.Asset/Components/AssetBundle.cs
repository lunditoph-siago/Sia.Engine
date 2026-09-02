namespace Sia.Asset;

using Sia;
using Sia.Reactors;

[SiaBundle]
public partial record struct AssetBundle<TAsset>
{
    public Sid<EntityId> Id;
    public Sid<AssetName> Name;
    public AssetMetadata Metadata;
    public TAsset Asset;
}

public static class AssetBundle
{
    public static AssetBundle<TAsset> Create<TAsset, TAssetRecord>(
        TAssetRecord record, AssetLife life = AssetLife.Automatic)
        where TAsset : IAsset<TAsset, TAssetRecord>, IConstructable<TAsset, TAssetRecord>
        where TAssetRecord : IAssetRecord
    {
        var bundle = new AssetBundle<TAsset> {
            Name = new(new(record.Name ?? "")),
            Metadata = new AssetMetadata {
                AssetType = typeof(TAsset),
                AssetLife = life,
                AssetSource = record
            },
        };
        TAsset.Construct(record, out bundle.Asset);
        return bundle;
    }
}
