using Sia;
using Xunit;

namespace Sia.Asset.Tests;

public class AssetGraphTests
{
    private record struct Root;
    private record struct Branch;
    private record struct Leaf;

    private static Entity Asset<T>(World world) where T : struct
        => world.Create().Add(new AssetMetadata { AssetType = typeof(T), AssetLife = AssetLife.Persistent });

    [Fact]
    public void RecursiveLookupSearchesEveryBranch()
    {
        using var world = new World();
        var root = Asset<Root>(world);
        var empty = Asset<Branch>(world);
        var branch = Asset<Branch>(world);
        var leaf = Asset<Leaf>(world);
        root.Refer(empty);
        empty.Refer(Asset<Branch>(world));
        root.Refer(branch);
        branch.Refer(leaf);
        Assert.Equal(leaf, root.FindDependent<Leaf>(true));
        Assert.Equal(root, leaf.FindReferrer<Root>(true));
    }

    [Fact]
    public void RecursiveReverseLookupSearchesEveryBranch()
    {
        using var world = new World();
        var root = Asset<Root>(world);
        var empty = Asset<Branch>(world);
        var branch = Asset<Branch>(world);
        var leaf = Asset<Leaf>(world);
        empty.Refer(leaf);
        Asset<Branch>(world).Refer(empty);
        branch.Refer(leaf);
        root.Refer(branch);
        Assert.Equal(root, leaf.FindReferrer<Root>(true));
    }

    [Fact]
    public void RecursiveEnumerationVisitsSharedDependencyOnce()
    {
        using var world = new World();
        var root = Asset<Root>(world);
        var a = Asset<Branch>(world);
        var b = Asset<Branch>(world);
        var leaf = Asset<Leaf>(world);
        root.Refer(a);
        root.Refer(b);
        a.Refer(leaf);
        b.Refer(leaf);
        Assert.Equal([leaf], root.GetDependents<Leaf>(true));
        Assert.Equal([root], leaf.GetReferrers<Root>(true));
    }

    [Fact]
    public void RecursiveLookupTerminatesOnCyclesAndReturnsEachReachableEntityOnce()
    {
        using var world = new World();
        var root = Asset<Root>(world);
        var branch = Asset<Branch>(world);
        root.Refer(branch);
        branch.Refer(root);
        Assert.Null(root.FindDependent<Leaf>(true));
        Assert.Null(root.FindReferrer<Leaf>(true));
        Assert.Equal([root], root.GetDependents<Root>(true));
        Assert.Equal([root], root.GetReferrers<Root>(true));
        Assert.Equal([branch], root.GetDependents<Branch>(true));
    }

    [Fact]
    public void NonRecursiveLookupOnlyReturnsDirectNeighbors()
    {
        using var world = new World();
        var root = Asset<Root>(world);
        var branch = Asset<Branch>(world);
        var leaf = Asset<Leaf>(world);
        root.Refer(branch);
        branch.Refer(leaf);
        Assert.Null(root.FindDependent<Leaf>());
        Assert.Empty(root.GetDependents<Leaf>());
        Assert.Equal(branch, root.GetDependent<Branch>());
        Assert.Throws<AssetNotFoundException>(() => root.GetDependent<Leaf>());
    }

    [Fact]
    public void UnreferMaintainsBothSidesAndIsIdempotent()
    {
        using var world = new World();
        var root = Asset<Root>(world);
        var leaf = Asset<Leaf>(world);
        root.Refer(leaf);
        root.Refer(leaf);
        Assert.Single(root.Get<AssetMetadata>().Dependents);
        Assert.Single(leaf.Get<AssetMetadata>().Referrers);
        root.Unrefer(leaf);
        root.Unrefer(leaf);
        Assert.Empty(root.Get<AssetMetadata>().Dependents);
        Assert.Empty(leaf.Get<AssetMetadata>().Referrers);
    }
}
