namespace Sia.Asset;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Sia;

public record struct AssetMetadata()
{
    public readonly record struct OnReferred(Entity Entity) : IEvent;
    public readonly record struct OnUnreferred(Entity Entity) : IEvent;

    public required Type AssetType { get; init; }
    public AssetLife AssetLife { get; init; }
    public IAssetRecord? AssetSource { get; init; }

    public readonly IReadOnlySet<Entity> Referrers =>
        _referrers ?? (IReadOnlySet<Entity>)ImmutableHashSet<Entity>.Empty;
    public readonly IReadOnlySet<Entity> Dependents =>
        _dependents ?? (IReadOnlySet<Entity>)ImmutableHashSet<Entity>.Empty;

    private HashSet<Entity>? _referrers;
    private HashSet<Entity>? _dependents;

    public readonly record struct Refer(Entity Asset) : ICommand<AssetMetadata>
    {
        public void Execute(World world, Entity target)
            => Execute(world, target, ref target.Get<AssetMetadata>());

        public void Execute(World world, Entity target, ref AssetMetadata metadata)
        {
            ref var dependents = ref metadata._dependents;
            dependents ??= [];
            if (!dependents.Add(Asset)) {
                return;
            }

            ref var referers = ref Asset.Get<AssetMetadata>()._referrers;
            referers ??= [];
            referers.Add(target);

            world.Send(Asset, new OnReferred(Asset));
        }
    }

    public readonly record struct Unrefer(Entity Asset) : ICommand<AssetMetadata>
    {
        public void Execute(World world, Entity target)
            => Execute(world, target, ref target.Get<AssetMetadata>());

        public void Execute(World world, Entity target, ref AssetMetadata metadata)
        {
            ref var dependents = ref metadata._dependents;
            if (dependents == null || !dependents.Remove(Asset)) {
                return;
            }
            Asset.Get<AssetMetadata>()._referrers!.Remove(target);
            world.Send(Asset, new OnUnreferred(Asset));
        }
    }

    public readonly Entity? FindReferrer<TAsset>(bool recurse = false)
        where TAsset : struct
    {
        if (_referrers == null) {
            return null;
        }

        var assetType = typeof(TAsset);

        if (recurse) {
            foreach (var referrer in _referrers) {
                ref var meta = ref referrer.Get<AssetMetadata>();
                if (meta.AssetType.IsAssignableTo(assetType)) {
                    return referrer;
                }
                if (meta._referrers != null) {
                    return meta.FindReferrer<TAsset>(recurse: true);
                }
            }
        }
        else {
            foreach (var referrer in _referrers) {
                if (referrer.Get<AssetMetadata>().AssetType.IsAssignableTo(assetType)) {
                    return referrer;
                }
            }
        }
        return null;
    }

    public readonly Entity GetReferrer<TAsset>(bool recurse = false)
        where TAsset : struct
        => FindReferrer<TAsset>(recurse) ?? ThrowAssetNotFound<TAsset>();

    public readonly IEnumerable<Entity> GetReferrers<TAsset>(bool recurse = false)
        where TAsset : struct
    {
        if (_referrers == null) {
            yield break;
        }

        var assetType = typeof(TAsset);

        if (recurse) {
            foreach (var referrer in _referrers) {
                var meta = referrer.Get<AssetMetadata>();
                if (meta.AssetType.IsAssignableTo(assetType)) {
                    yield return referrer;
                }
                if (meta._referrers != null) {
                    foreach (var found in meta.GetReferrers<TAsset>(recurse: true)) {
                        yield return found;
                    }
                }
            }
        }
        else {
            foreach (var referrer in _referrers) {
                if (referrer.Get<AssetMetadata>().AssetType.IsAssignableTo(assetType)) {
                    yield return referrer;
                }
            }
        }
    }

    public readonly Entity? FindDependent<TAsset>(bool recurse = false)
        where TAsset : struct
    {
        if (_dependents == null) {
            return null;
        }

        var assetType = typeof(TAsset);

        if (recurse) {
            foreach (var dependent in _dependents) {
                ref var meta = ref dependent.Get<AssetMetadata>();
                if (meta.AssetType.IsAssignableTo(assetType)) {
                    return dependent;
                }
                if (meta._dependents != null) {
                    return meta.FindDependent<TAsset>(recurse: true);
                }
            }
        }
        else {
            foreach (var dependent in _dependents) {
                if (dependent.Get<AssetMetadata>().AssetType.IsAssignableTo(assetType)) {
                    return dependent;
                }
            }
        }
        return null;
    }

    public readonly Entity GetDependent<TAsset>(bool recurse = false)
        where TAsset : struct
        => FindDependent<TAsset>(recurse) ?? ThrowAssetNotFound<TAsset>();

    public readonly IEnumerable<Entity> GetDependents<TAsset>(bool recurse = false)
        where TAsset : struct
    {
        if (_dependents == null) {
            yield break;
        }

        var assetType = typeof(TAsset);

        if (recurse) {
            foreach (var dependent in _dependents) {
                var meta = dependent.Get<AssetMetadata>();
                if (meta.AssetType.IsAssignableTo(assetType)) {
                    yield return dependent;
                }
                if (meta._dependents != null) {
                    foreach (var found in meta.GetDependents<TAsset>(recurse: true)) {
                        yield return found;
                    }
                }
            }
        }
        else {
            foreach (var dependent in _dependents) {
                if (dependent.Get<AssetMetadata>().AssetType.IsAssignableTo(assetType)) {
                    yield return dependent;
                }
            }
        }
    }

    [DoesNotReturn]
    private static Entity ThrowAssetNotFound<TAsset>()
        => throw new AssetNotFoundException("Asset not found: " + typeof(TAsset));
}
