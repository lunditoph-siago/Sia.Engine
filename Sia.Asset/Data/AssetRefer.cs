namespace Sia.Asset;

using System.Runtime.CompilerServices;
using Sia.Reactors;

public abstract record AssetRefer<TAssetRecord>
    where TAssetRecord : IAssetRecord
{
    public sealed record Record(TAssetRecord Value) : AssetRefer<TAssetRecord>;
    public sealed record Entity(Sia.Entity Value) : AssetRefer<TAssetRecord>;
    public sealed record Name(string Value) : AssetRefer<TAssetRecord>;
    public sealed record Matcher(AssetMatcher Value) : AssetRefer<TAssetRecord>;

    public static implicit operator AssetRefer<TAssetRecord>(TAssetRecord record) => new Record(record);
    public static implicit operator AssetRefer<TAssetRecord>(Sia.Entity entity) => new Entity(entity);
    public static implicit operator AssetRefer<TAssetRecord>(string name) => new Name(name);
    public static implicit operator AssetRefer<TAssetRecord>(AssetMatcher matcher) => new Matcher(matcher);

    public Sia.Entity? Find(World world)
    {
        var entity = DoFind(world);
        if (entity == null) {
            return null;
        }
        ref var meta = ref entity.Value.GetOrNullRef<AssetMetadata>();
        if (Unsafe.IsNullRef(ref meta) || !meta.AssetType.IsAssignableTo(typeof(IAsset<TAssetRecord>))) {
            return null;
        }
        return entity;
    }

    private Sia.Entity? DoFind(World world)
    {
        switch (this) {
            case Record(var record): {
                var assetLib = world.GetAddon<AssetLibrary>();
                return assetLib.TryGet(record, out var entity) ? entity : null;
            }
            case Name(var name): {
                var aggr = world.GetAddon<Aggregator<AssetName>>();
                return aggr.TryGet(new(name), out var group)
                    ? group.Get<Aggregation<AssetName>>().First : null;
            }
            case Entity e:
                return e.Value.IsValid ? e.Value : null;
            case Matcher e: {
                var query = world.Query(e.Value.Matcher);
                foreach (var host in query.Hosts) {
                    foreach (var entity in host) {
                        return entity;
                    }
                }
                return null;
            }
            default:
                return null;
        }
    }
}
