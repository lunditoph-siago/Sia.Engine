namespace Sia.Asset;

public interface IAsset<TAssetRecord>
{
}

public interface IAsset<TAsset, TAssetRecord>
    : IAsset<TAssetRecord>, IGeneratedByTemplate<TAsset, TAssetRecord>
    where TAsset : IAsset<TAsset, TAssetRecord>
    where TAssetRecord : IAssetRecord
{
    abstract static AssetBundle<TAsset> Create(
        TAssetRecord record, AssetLife life = AssetLife.Automatic);
}