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

    public static Entity AcquireAsset<TAssetRecord>(this World world, AssetId id,
        Func<TAssetRecord> createRecord, AssetLife life = AssetLife.Persistent)
        where TAssetRecord : class, IAssetRecord
        => world.GetAddon<AssetLibrary>().AcquireEntity(id, createRecord, life);

    public static Entity AcquireAsset<TAssetRecord>(this World world, AssetId id,
        Func<TAssetRecord> createRecord, Entity referrer, AssetLife life = AssetLife.Automatic)
        where TAssetRecord : class, IAssetRecord
        => world.GetAddon<AssetLibrary>().AcquireEntity(id, createRecord, referrer, life);

    public static bool TryGetAsset<TAssetRecord>(this World world, AssetId id, out Entity entity)
        where TAssetRecord : IAssetRecord
        => world.GetAddon<AssetLibrary>().TryGet<TAssetRecord>(id, out entity);
}
