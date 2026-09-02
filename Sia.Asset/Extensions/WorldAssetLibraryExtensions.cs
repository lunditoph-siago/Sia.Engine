namespace Sia.Asset;

public static class WorldAssetLibraryExtensions
{
    public static Entity CreateAsset(
        this World world, IAssetRecord record, AssetLife life = AssetLife.Automatic)
        => world.GetAddon<AssetLibrary>().CreateEntity(record, life);

    public static Entity CreateAsset(
        this World world, IAssetRecord record, Entity referrer, AssetLife life = AssetLife.Automatic)
        => world.GetAddon<AssetLibrary>().CreateEntity(record, referrer, life);

    public static Entity AcquireAsset(
        this World world, IAssetRecord record, AssetLife life = AssetLife.Persistent)
        => world.GetAddon<AssetLibrary>().AcquireEntity(record, life);

    public static Entity AcquireAsset(
        this World world, IAssetRecord record, Entity referrer, AssetLife life = AssetLife.Automatic)
        => world.GetAddon<AssetLibrary>().AcquireEntity(record, referrer, life);

    public static Entity GetAsset(this World world, IAssetRecord record)
        => world.GetAddon<AssetLibrary>()[record];

    public static bool TryGetAsset(this World world, IAssetRecord record, out Entity entity)
        => world.GetAddon<AssetLibrary>().TryGet(record, out entity);
}
