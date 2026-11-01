using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;
using Xunit.Abstractions;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrSceneStreamLayoutTests(ITestOutputHelper output)
{
    private sealed record Fixture(MeshSource Source, MeshVirtualPagesData Index,
        IReadOnlyDictionary<AssetId, MeshVirtualPageData> Pages);
    private static readonly Lazy<Fixture> Input = new(CreateFixture);

    [Fact]
    public void BorrowedVirtualNodesPreservePartLocalLinksAndGlobalRangeSlices()
    {
        var fixture = Input.Value;
        var scene = Scene([fixture.Index], repeatedInstance: true);
        var stream = Create(scene, fixture.Pages);
        Assert.Equal(3, stream.SourceInstanceCount);
        Assert.Equal(2, stream.Hierarchies.Length);
        Assert.Equal(stream.Instances.Span[0].AssetIndex, stream.Instances.Span[2].AssetIndex);
        Assert.True(fixture.Index.Nodes[fixture.Index.Parts[1].FirstNode].FirstRange > 0);
        for (var partIndex = 0; partIndex < fixture.Index.Parts.Length; partIndex++) {
            var part = fixture.Index.Parts[partIndex]; var tree = stream.Hierarchies[partIndex];
            var source = fixture.Index.Nodes.Slice(part.FirstNode, part.NodeCount);
            Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(source), ref MemoryMarshal.GetReference(tree.Nodes)));
            Assert.Equal(part.RootCount, tree.Roots);
            Assert.Equal(source.ToArray(), tree.Nodes.ToArray());
            var ranges = 0;
            foreach (var node in tree.Nodes) {
                var pages = tree.GetPages(node);
                Assert.Equal(node.RangeCount, pages.Length);
                for (var r = 0; r < pages.Length; r++) {
                    var expected = fixture.Index.Ranges[node.FirstRange + r];
                    var descriptor = fixture.Index.Pages[expected.Page];
                    var page = stream.Pages[pages[r].Page];
                    Assert.Equal(descriptor.Id.Value.ToString("N"), page.Id);
                    Assert.Equal(pages[r].Page, stream.GetPageIndex(page.Id));
                    Assert.Equal(expected.FirstTriangle, pages[r].First);
                    Assert.Equal(expected.TriangleCount, pages[r].Count);
                    ranges++;
                }
            }
            Assert.Equal(ranges, tree.Parts.Length);
        }
        Assert.Equal(scene.Instances.Span[2].Transform, stream.Instances.Span[2].Transform);
        Assert.Equal(scene.Instances.Span[2].AuthoredId, stream.Instances.Span[2].AuthoredId);
    }

    [Fact]
    public void RootShapePreservesDistinctLogicalPagesAndSharedContentAliases()
    {
        var fixture = Input.Value;
        var second = MeshVirtualPagesData.Build(fixture.Source, new(new Guid("64000000-0000-0000-0000-000000000002")),
            fixture.Index.Settings);
        var pages = fixture.Pages.Concat(second.Pages).ToDictionary(pair => pair.Key, pair => pair.Value);
        var scene = Scene([fixture.Index, second.Index], repeatedInstance: true);
        var stream = Create(scene, pages);
        Assert.Contains(stream.ResidentRoots.GroupBy(pair => pair.Value.Data!.ContentHash), group => group.Count() > 1);
        Assert.Equal(stream.Pages.Length, stream.Pages.Select(page => page.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var shared in new[] { false, true }) {
            var expected = ReferenceRootShape(scene, pages, shared);
            var actual = PbrGpuScene.RootShape(stream, shared);
            Assert.Equal(expected.Vertices, actual.Vertices);
            Assert.Equal(expected.Triangles, actual.Triangles);
        }
        foreach (var tree in stream.Hierarchies) {
            var actual = PbrGpuHierarchy.MaximumCut(tree);
            var expected = ReferenceMaximumCut(tree, 0, tree.Roots);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void TransparentFinestRangesPreserveAuthoredBindings()
    {
        var fixture = Input.Value;
        var scene = Scene([fixture.Index], repeatedInstance: true, transparent: true);
        var stream = Create(scene, fixture.Pages);
        Assert.Empty(stream.Hierarchies);
        Assert.Empty(stream.Pages);
        Assert.Empty(stream.ResidentRoots);
        Assert.True(stream.Instances.IsEmpty);
        var at = 0;
        foreach (var instance in scene.Instances.Span) {
            var geometry = scene.Geometry.Span[instance.Geometry]; var part = geometry.Product.Parts[geometry.Part];
            foreach (var node in geometry.Product.Nodes.Slice(part.FirstNode, part.NodeCount)) {
                if (node.ChildCount != 0) continue;
                foreach (var range in geometry.Product.Ranges.Slice(node.FirstRange, node.RangeCount)) {
                    var actual = stream.TransparentInstances[at++];
                    var page = stream.TransparentGeometry[actual.Geometry];
                    Assert.Equal(instance.Transform, actual.Transform);
                    Assert.Equal(instance.AuthoredId, actual.AuthoredId);
                    Assert.Equal(instance.MaterialSlot, actual.MaterialSlot);
                    Assert.Equal(instance.Material, actual.Material);
                    Assert.Equal(range.FirstTriangle, page.FirstTriangle);
                    Assert.Equal(range.TriangleCount, page.TriangleCount);
                    Assert.Same(fixture.Pages[geometry.Product.Pages[range.Page].Id], page.Page.Data);
                }
            }
        }
        Assert.Equal(at, stream.TransparentInstances.Length);
    }

    [Fact]
    public void MixedRasterRangesKeepExactGeometryAndDistinctPageIndices()
    {
        var fixture = Input.Value;
        var virtualScene = Scene([fixture.Index], repeatedInstance: false);
        var raster = MeshRasterData.Build(fixture.Source, MeshRasterBuildSettings.Default with { MaximumLods = 1 });
        var rasterScene = PbrRasterScene.Create([new(raster, 0), new(raster, 1)], [new(PbrMaterial.Default)],
            [new(0, 0, float4x4.identity), new(1, 0, float4x4.identity)]);
        var mixed = new PbrMixedScene(rasterScene, virtualScene, [1, 2, 3]);
        var stream = PbrSceneStream.Create(mixed, r => fixture.Pages[r.Value],
            (r, _) => Task.FromResult(new MeshVirtualPageRecord { Data = fixture.Pages[r.Value] }), (_, r) => r.Data);
        Assert.True(stream.RequiresInstancing);
        Assert.Equal(4, stream.SourceInstanceCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, stream.Identity.ToArray());
        var rasterTriangles = 0;
        for (var h = virtualScene.Geometry.Length; h < stream.Hierarchies.Length; h++) {
            var tree = stream.Hierarchies[h];
            foreach (var node in tree.Nodes) {
                Assert.Equal(-1, node.Parent);
                Assert.Equal(0, node.ChildCount);
                Assert.Equal(0, node.Error);
                var part = Assert.Single(tree.GetPages(node).ToArray());
                var info = stream.Pages[part.Page];
                Assert.StartsWith("raster/", info.Id);
                Assert.Equal(part.Page, stream.GetPageIndex(info.Id));
                Assert.Equal(node.TriangleCount, part.Count);
                Assert.Equal(part.Count, stream.ResidentRoots[info.Id].TriangleCount);
                rasterTriangles += part.Count;
            }
        }
        Assert.Equal(fixture.Source.Indices.Length / 3, rasterTriangles);
    }

    [Fact]
    public void ActualAcquiredPageStillMustMatchTheParentRevision()
    {
        var fixture = Input.Value; var stream = Create(Scene([fixture.Index], false), fixture.Pages);
        var page = stream.Pages[0];
        var incompatible = MeshVirtualPageData.Create(ProceduralMesh.Cube(size: 10).Vertices,
            ProceduralMesh.Cube(size: 10).Indices);
        var mismatched = PbrSceneStream.Create(Scene([fixture.Index], false), r => fixture.Pages[r.Value],
            (r, _) => Task.FromResult(new MeshVirtualPageRecord { Data = fixture.Pages[r.Value] }), (_, _) => incompatible);
        Assert.Throws<InvalidDataException>(() => mismatched.UsePage(page.Id, new() { Data = fixture.Pages.Values.First() }));
    }

    [Fact]
    public void CompactHierarchyReusesNodesAndAllocatesNoTraversalObjects()
    {
        var fixture = Input.Value; var part = fixture.Index.Parts[0];
        _ = BuildCompact(fixture.Index, 0); _ = BuildLegacy(fixture.Index, 0);
        const int iterations = 64;
        var start = Stopwatch.GetTimestamp(); var before = GC.GetAllocatedBytesForCurrentThread();
        PbrSceneStream.HierarchyInfo? compact = null;
        for (var i = 0; i < iterations; i++) compact = BuildCompact(fixture.Index, 0);
        var compactBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var compactMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = Stopwatch.GetTimestamp(); before = GC.GetAllocatedBytesForCurrentThread();
        LegacyNode[]? legacy = null;
        for (var i = 0; i < iterations; i++) legacy = BuildLegacy(fixture.Index, 0);
        var legacyBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var legacyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Assert.True(compactBytes < legacyBytes / 2, $"compact={compactBytes}, legacy={legacyBytes}");
        long sum = 0;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            foreach (var node in compact!.Nodes)
                foreach (var page in compact.GetPages(node)) sum += page.Count;
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(sum > 0);
        output.WriteLine($"Metadata-only controlled comparison: {part.NodeCount} nodes, {compact!.Parts.Length} ranges, {iterations} iterations; compact {compactBytes} B/{compactMs:F3} ms, legacy {legacyBytes} B/{legacyMs:F3} ms. Source build and page payloads excluded; elapsed time is observational, not a gate.");
        GC.KeepAlive(compact); GC.KeepAlive(legacy);
    }

    private static PbrSceneStream.HierarchyInfo BuildCompact(MeshVirtualPagesData index, int partIndex)
    {
        var part = index.Parts[partIndex]; var nodes = index.Nodes.Slice(part.FirstNode, part.NodeCount);
        var first = nodes[0].FirstRange; var end = nodes[^1].FirstRange + nodes[^1].RangeCount;
        var pages = new PbrSceneStream.PagePart[end - first];
        for (var i = 0; i < pages.Length; i++) {
            var range = index.Ranges[first + i]; pages[i] = new(range.Page, range.FirstTriangle, range.TriangleCount);
        }
        return new(index, partIndex, pages);
    }

    // Former runtime representation, retained only as a semantic/allocation control.
    private sealed record LegacyPart(string Id, int First, int Count);
    private sealed record LegacyNode(int Parent, int Children, int ChildCount, float Error,
        float[] Bounds, LegacyPart[] Pages, int Triangles);
    private static LegacyNode[] BuildLegacy(MeshVirtualPagesData index, int partIndex)
    {
        var part = index.Parts[partIndex]; var result = new LegacyNode[part.NodeCount];
        for (var n = 0; n < result.Length; n++) {
            var node = index.Nodes[part.FirstNode + n]; var pages = new LegacyPart[node.RangeCount];
            for (var r = 0; r < pages.Length; r++) {
                var range = index.Ranges[node.FirstRange + r];
                pages[r] = new(index.Pages[range.Page].Id.Value.ToString("N"), range.FirstTriangle, range.TriangleCount);
            }
            var b = node.Bounds;
            result[n] = new(node.Parent, node.FirstChild, node.ChildCount, node.Error,
                [b.Min.x, b.Min.y, b.Min.z, b.Max.x, b.Max.y, b.Max.z], pages, node.TriangleCount);
        }
        return result;
    }

    private static (ulong Vertices, ulong Triangles) ReferenceRootShape(PbrVirtualScene scene,
        IReadOnlyDictionary<AssetId, MeshVirtualPageData> pages, bool shared)
    {
        ulong vertices = 0, triangles = 0; var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in scene.Instances.Span) {
            var geometry = scene.Geometry.Span[instance.Geometry]; var index = geometry.Product; var part = index.Parts[geometry.Part];
            var seen = new HashSet<AssetId>();
            foreach (var node in index.Nodes.Slice(part.FirstNode, part.RootCount))
                foreach (var range in index.Ranges.Slice(node.FirstRange, node.RangeCount)) {
                    var descriptor = index.Pages[range.Page];
                    if (!seen.Add(descriptor.Id) || shared && !hashes.Add(pages[descriptor.Id].ContentHash)) continue;
                    vertices += (uint)descriptor.VertexCount; triangles += (uint)descriptor.TriangleCount;
                }
        }
        return (vertices, triangles);
    }

    private static (ulong Triangles, ulong Nodes, ulong Records) ReferenceMaximumCut(PbrSceneStream.HierarchyInfo tree, int first, int count)
    {
        ulong triangles = 0, nodes = 0, records = 0;
        for (var n = first; n < first + count; n++) {
            var node = tree.Nodes[n]; var child = ReferenceMaximumCut(tree, node.FirstChild, node.ChildCount);
            ulong ownRecords = 0;
            foreach (var part in tree.GetPages(node)) ownRecords += ((ulong)part.Count + PbrGpuHierarchy.WorkBlockTriangles - 1) / PbrGpuHierarchy.WorkBlockTriangles;
            triangles += System.Math.Max((ulong)node.TriangleCount, child.Triangles);
            nodes += System.Math.Max(1ul, child.Nodes); records += System.Math.Max(ownRecords, child.Records);
        }
        return (triangles, nodes, records);
    }

    private static PbrSceneStream Create(PbrVirtualScene scene, IReadOnlyDictionary<AssetId, MeshVirtualPageData> pages)
        => PbrSceneStream.Create(scene, r => pages[r.Value],
            (r, _) => Task.FromResult(new MeshVirtualPageRecord { Data = pages[r.Value] }), (_, r) => r.Data);

    private static PbrVirtualScene Scene(MeshVirtualPagesData[] indexes, bool repeatedInstance, bool transparent = false)
    {
        var geometry = new List<PbrVirtualGeometry>(); var instances = new List<PbrSceneInstance>();
        foreach (var index in indexes)
            for (var part = 0; part < index.Parts.Length; part++) {
                instances.Add(new(geometry.Count, part, float4x4.identity) { AuthoredId = Guid.NewGuid(), MaterialSlot = part });
                geometry.Add(new(index, part));
            }
        if (repeatedInstance) instances.Add(instances[0] with { Transform = float4x4.Translate(new float3(3, 2, 1)) });
        return new(geometry.ToArray(), [new(PbrMaterial.Default, AlphaBlend: transparent), new(PbrMaterial.Default, AlphaBlend: transparent)],
            instances.ToArray(), "layout-control", [1]);
    }

    private static Fixture CreateFixture()
    {
        var mesh = ProceduralMesh.Sphere(latitudeSegments: 8, longitudeSegments: 16);
        var half = mesh.Indices.Length / 6 * 3;
        var source = MeshSource.Create(mesh, [new(0, half, 0), new(half, mesh.Indices.Length - half, 1)]);
        var result = MeshVirtualPagesData.Build(source, new(new Guid("64000000-0000-0000-0000-000000000001")),
            MeshPatchBuildSettings.Default with { MaxLeafTriangles = 8 });
        return new(source, result.Index, result.Pages);
    }
}
