using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrGeometryAdmissionLayoutTests
{
    [Fact]
    public void MultipleHierarchiesKeepParentsLocalAndNodeSlotsDistinct()
    {
        var tree = Tree();
        var layout = new PbrGeometryAdmissionLayout([tree, Tree()], [0, 1, 2, 3]);
        Assert.Equal(6, layout.NodeCount);
        Assert.Equal(0, layout.Node(0, 0)); Assert.Equal(3, layout.Node(1, 0));
        for (var asset = 0; asset < 2; asset++) {
            Assert.Equal(-1, layout.ParentLocal(asset, 0));
            Assert.Equal(0, layout.ParentLocal(asset, 1));
            Assert.Equal(0, layout.ParentLocal(asset, 2));
            Assert.Equal(new[] { 0, 1 }, layout.Pages(asset, 0).ToArray());
            Assert.Equal(new[] { 1, 2 }, layout.Pages(asset, 1).ToArray());
            Assert.Equal(new[] { 2, 3 }, layout.Pages(asset, 2).ToArray());
        }
    }

    [Fact]
    public void CanonicalAliasesDeduplicateWithinNodesAndKeepCrossNodeDependencies()
    {
        var layout = new PbrGeometryAdmissionLayout([Tree()], [0, 0, 2, 2]);
        Assert.Equal(new[] { 0 }, layout.Pages(0, 0).ToArray());
        Assert.Equal(new[] { 0, 2 }, layout.Pages(0, 1).ToArray());
        Assert.Equal(new[] { 2 }, layout.Pages(0, 2).ToArray());
        var set = new PbrAdmissionEpochSet(4); set.Begin();
        var costs = new[] { 3, 100, 5, 100 }; var cost = 0;
        foreach (var node in new[] { 0, 1, 2 })
            foreach (var page in layout.Pages(0, node)) if (set.Add(page)) cost += costs[page];
        Assert.Equal(8, cost);
    }

    [Fact]
    public void EpochResetAndUnsignedWrapCannotRetainStaleMembership()
    {
        var empty = new PbrAdmissionEpochSet(4);
        Assert.False(empty.Contains(0));
        Assert.Throws<InvalidOperationException>(() => empty.Add(0));
        empty.Begin(); Assert.True(empty.Add(0)); Assert.False(empty.Add(0));
        empty.Begin(); Assert.False(empty.Contains(0)); Assert.True(empty.Add(0));
        var wrapped = new PbrAdmissionEpochSet(4, uint.MaxValue - 1);
        wrapped.Begin(); Assert.True(wrapped.Add(2)); Assert.True(wrapped.Contains(2));
        wrapped.Begin(); Assert.False(wrapped.Contains(2)); Assert.True(wrapped.Add(2));
        Assert.False(wrapped.Contains(1)); Assert.True(wrapped.Add(1));
    }

    [Fact]
    public void CanonicalRangesRejectMissingPagesAndForwardParentLinks()
    {
        Assert.Throws<ArgumentException>(() => new PbrGeometryAdmissionLayout([Tree()], [0, 1, 2]));
        Assert.Throws<ArgumentException>(() => new PbrGeometryAdmissionLayout([Tree()], [0, 1, 2, 4]));
        var nodes = new[] { new MeshVirtualPageNode(new(new float3(-1), new float3(1)), 0, 0, 0, 0, 0, 1, 1) };
        var invalid = new PbrSceneStream.HierarchyInfo(1, nodes, [new(0, 0, 1)]);
        Assert.Throws<ArgumentException>(() => new PbrGeometryAdmissionLayout([invalid], [0]));
    }

    [Fact]
    public void RepeatedDependencyTraversalAndEpochMembershipAllocateNothing()
    {
        var layout = new PbrGeometryAdmissionLayout([Tree(), Tree()], [0, 1, 2, 3]);
        var seen = new PbrAdmissionEpochSet(4); seen.Begin(); seen.Add(0);
        var before = GC.GetAllocatedBytesForCurrentThread(); long total = 0;
        for (var iteration = 0; iteration < 1024; iteration++) {
            seen.Begin();
            for (var asset = 0; asset < 2; asset++)
                for (var node = 0; node < 3; node++) {
                    total += layout.ParentLocal(asset, node) + 1;
                    foreach (var page in layout.Pages(asset, node)) if (seen.Add(page)) total += page;
                }
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(10240, total);
    }

    private static PbrSceneStream.HierarchyInfo Tree()
    {
        var bounds = new Aabb(new float3(-1), new float3(1));
        return new(1, [new(bounds, 1, -1, 1, 2, 0, 2, 2), new(bounds, 0, 0, 3, 0, 2, 2, 2),
            new(bounds, 0, 0, 3, 0, 4, 2, 2)],
            [new(0, 0, 1), new(1, 0, 1), new(1, 0, 1), new(2, 0, 1), new(2, 0, 1), new(3, 0, 1)]);
    }
}
