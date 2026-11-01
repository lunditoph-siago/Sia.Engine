namespace Sia.Asset;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Sia;

public record struct AssetMetadata()
{
    public readonly record struct OnReferred(Entity Entity) : IEvent;
    public readonly record struct OnUnreferred(Entity Entity) : IEvent;

    public required Type AssetType { get; init; }
    public AssetLife AssetLife { get; init; }
    public IAssetRecord? AssetSource { get; init; }
    public AssetId? Id { get; internal set; }

    public readonly IReadOnlySet<Entity> Referrers =>
        _referrers ?? (IReadOnlySet<Entity>)ImmutableHashSet<Entity>.Empty;
    public readonly IReadOnlySet<Entity> Dependents =>
        _dependents ?? (IReadOnlySet<Entity>)ImmutableHashSet<Entity>.Empty;

    private HashSet<Entity>? _referrers;
    private HashSet<Entity>? _dependents;

    public readonly record struct Refer(Entity Asset) : ICommand<AssetMetadata>
    {
        public void Execute(World world, Entity target)
        {
            RequireAsset(world, target, nameof(target));
            Execute(world, target, ref target.Get<AssetMetadata>());
        }

        public void Execute(World world, Entity target, ref AssetMetadata metadata)
        {
            RequireAsset(world, target, nameof(target));
            RequireAsset(world, Asset, nameof(Asset));
            if (metadata._dependents?.Contains(Asset) == true) return;
            if (world.TryGetAddon<AssetLibrary>(out var library)
                && (library.IsReleasing(target) || library.IsReleasing(Asset)))
                throw new InvalidOperationException("An asset being released cannot acquire references.");
            if (Asset == target || Reaches(Asset, target))
                throw new InvalidOperationException("Asset references must form a directed acyclic graph.");

            ref var dependents = ref metadata._dependents;
            dependents ??= [];
            dependents.Add(Asset);

            ref var referers = ref Asset.Get<AssetMetadata>()._referrers;
            referers ??= [];
            referers.Add(target);

            world.Send(Asset, new OnReferred(Asset));
        }
    }

    public readonly record struct Unrefer(Entity Asset) : ICommand<AssetMetadata>
    {
        public void Execute(World world, Entity target)
        {
            RequireAsset(world, target, nameof(target));
            Execute(world, target, ref target.Get<AssetMetadata>());
        }

        public void Execute(World world, Entity target, ref AssetMetadata metadata)
        {
            RequireAsset(world, target, nameof(target));
            RequireAsset(world, Asset, nameof(Asset));
            ref var dependents = ref metadata._dependents;
            if (dependents == null || !dependents.Remove(Asset)) {
                return;
            }
            Asset.Get<AssetMetadata>()._referrers!.Remove(target);
            world.Send(Asset, new OnUnreferred(Asset));
        }
    }

    internal static void RequireAsset(World world, Entity entity, string parameterName)
    {
        world.EnsureOwnerThread();
        if (!entity.IsValid || !ReferenceEquals(entity.Host.World, world)
            || Unsafe.IsNullRef(ref entity.GetOrNullRef<AssetMetadata>()))
            throw new ArgumentException("The entity must be a live asset in this world.", parameterName);
    }

    private static bool Reaches(Entity start, Entity destination)
    {
        var dependents = start.Get<AssetMetadata>().Dependents;
        if (dependents.Count == 0) return false;
        var pending = new Stack<Entity>(dependents);
        var visited = new HashSet<Entity>();
        while (pending.TryPop(out var current)) {
            if (current == destination) return true;
            if (!current.IsValid || !visited.Add(current)) continue;
            foreach (var dependent in current.Get<AssetMetadata>().Dependents)
                pending.Push(dependent);
        }
        return false;
    }

    public readonly Entity? FindReferrer<TAsset>(bool recurse = false)
        where TAsset : struct
    {
        foreach (var entity in GetReferrers<TAsset>(recurse)) return entity;
        return null;
    }

    public readonly Entity GetReferrer<TAsset>(bool recurse = false)
        where TAsset : struct
        => FindReferrer<TAsset>(recurse) ?? ThrowAssetNotFound<TAsset>();

    public readonly IEnumerable<Entity> GetReferrers<TAsset>(bool recurse = false)
        where TAsset : struct
        => Enumerate<TAsset>(_referrers, recurse, referrers: true);

    public readonly Entity? FindDependent<TAsset>(bool recurse = false)
        where TAsset : struct
    {
        foreach (var entity in GetDependents<TAsset>(recurse)) return entity;
        return null;
    }

    public readonly Entity GetDependent<TAsset>(bool recurse = false)
        where TAsset : struct
        => FindDependent<TAsset>(recurse) ?? ThrowAssetNotFound<TAsset>();

    public readonly IEnumerable<Entity> GetDependents<TAsset>(bool recurse = false)
        where TAsset : struct
        => Enumerate<TAsset>(_dependents, recurse, referrers: false);

    private static IEnumerable<Entity> Enumerate<TAsset>(
        HashSet<Entity>? neighbors, bool recurse, bool referrers)
        where TAsset : struct
    {
        if (neighbors is null) yield break;
        var assetType = typeof(TAsset);
        if (!recurse) {
            foreach (var entity in neighbors) {
                if (entity.IsValid && entity.Get<AssetMetadata>().AssetType.IsAssignableTo(assetType))
                    yield return entity;
            }
            yield break;
        }

        // Iterative depth-first traversal visits shared nodes and cycles once.
        var pending = new Stack<Entity>(neighbors.Reverse());
        var visited = new HashSet<Entity>();
        while (pending.TryPop(out var entity)) {
            if (!entity.IsValid || !visited.Add(entity)) continue;
            var metadata = entity.Get<AssetMetadata>();
            if (metadata.AssetType.IsAssignableTo(assetType)) yield return entity;
            var next = referrers ? metadata._referrers : metadata._dependents;
            if (next is not null) {
                foreach (var neighbor in next.Reverse()) pending.Push(neighbor);
            }
        }
    }

    [DoesNotReturn]
    private static Entity ThrowAssetNotFound<TAsset>()
        => throw new AssetNotFoundException("Asset not found: " + typeof(TAsset));
}
