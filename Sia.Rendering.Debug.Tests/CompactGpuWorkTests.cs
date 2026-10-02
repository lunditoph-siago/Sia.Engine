using Sia.Engine.Rendering.Pbr;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class CompactGpuWorkTests
{
    [Fact]
    public void RecordCapacityIncludesLargerParentCutAndAliasedShortParts()
    {
        var block = checked((int)PbrGpuHierarchy.WorkBlockTriangles);
        var tree = new PbrSceneStream.HierarchyInfo(1, [
            Node(-1, 1, 2, Enumerable.Range(0, block * 2).Select(i => new PbrSceneStream.PagePart("same", i, 1)).ToArray()),
            Node(0, 0, 0, [new("same", 0, block)]),
            Node(0, 0, 0, [new("same", block, block)])
        ]);
        var cut = PbrGpuHierarchy.MaximumCut(tree);
        Assert.Equal((ulong)block * 2, cut.Triangles);
        Assert.Equal((ulong)block * 2, cut.Records);
        Assert.Equal(2ul, cut.Nodes);
    }

    [Fact]
    public void RecordCapacityCountsEachPartialPartAndIndependentRoot()
    {
        var block = checked((int)PbrGpuHierarchy.WorkBlockTriangles);
        var tree = new PbrSceneStream.HierarchyInfo(2, [
            Node(-1, 0, 0, [new("page", 7, block + 1), new("page", 7, 1)]),
            Node(-1, 0, 0, [new("page", 7, 1)])
        ]);
        var cut = PbrGpuHierarchy.MaximumCut(tree);
        Assert.Equal((ulong)block + 3, cut.Triangles);
        Assert.Equal(4ul, cut.Records);
        Assert.Equal(2ul, cut.Nodes);
    }

    private static PbrSceneStream.NodeInfo Node(int parent, int children, int count,
        PbrSceneStream.PagePart[] parts)
        => new(parent, children, count, count == 0 ? 0 : 1, [0, 0, 0, 1, 1, 1],
            parts, parts.Sum(part => part.Count));
}
