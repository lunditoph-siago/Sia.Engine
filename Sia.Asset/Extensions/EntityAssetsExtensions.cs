namespace Sia.Asset;

public static class EntityAssetExtensions
{
    public static void Refer(this Entity entity, Entity target)
        => entity.Execute(new AssetMetadata.Refer(target));

    public static void Unrefer(this Entity entity, Entity target)
        => entity.Execute(new AssetMetadata.Unrefer(target));

    public static Entity? FindReferrer<TAsset>(this Entity entity, bool recurse = false)
        where TAsset : struct
        => entity.Get<AssetMetadata>().FindReferrer<TAsset>(recurse);

    public static Entity GetReferrer<TAsset>(this Entity entity, bool recurse = false)
        where TAsset : struct
        => entity.Get<AssetMetadata>().GetReferrer<TAsset>(recurse);

    public static IEnumerable<Entity> GetReferrers<TAsset>(this Entity entity, bool recurse = false)
        where TAsset : struct
        => entity.Get<AssetMetadata>().GetReferrers<TAsset>(recurse);

    public static Entity? FindDependent<TAsset>(this Entity entity, bool recurse = false)
        where TAsset : struct
        => entity.Get<AssetMetadata>().FindDependent<TAsset>(recurse);

    public static Entity GetDependent<TAsset>(this Entity entity, bool recurse = false)
        where TAsset : struct
        => entity.Get<AssetMetadata>().GetDependent<TAsset>(recurse);

    public static IEnumerable<Entity> GetDependents<TAsset>(this Entity entity, bool recurse = false)
        where TAsset : struct
        => entity.Get<AssetMetadata>().GetDependents<TAsset>(recurse);
}
