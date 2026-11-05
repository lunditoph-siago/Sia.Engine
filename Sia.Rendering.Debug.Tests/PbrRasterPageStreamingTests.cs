using System.Runtime.CompilerServices;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrRasterPageStreamingTests
{
    private sealed record Fixture(MeshRasterData Raster, MeshRasterPagesData Product,
        IReadOnlyDictionary<AssetId, MeshRasterPageData> Pages);
    private static readonly Lazy<Fixture> Input = new(CreateFixture);

    [Fact]
    public void BootstrapLoadsOnlyCoarseOpaqueIndicesAndTheirExactVertexDependencies()
    {
        var fixture = Input.Value; var scene = Scene(fixture.Product);
        var required = PbrSceneStream.RequiredPages(scene);
        var coarse = fixture.Product.GetLods(0)[^1];
        var expected = new HashSet<AssetId>();
        for (var page = coarse.FirstPage; page < coarse.FirstPage + coarse.PageCount; page++) {
            expected.Add(fixture.Product.Pages[page].Id);
            foreach (var vertex in fixture.Product.GetVertexDependencies(page)) expected.Add(fixture.Product.Pages[vertex].Id);
        }
        Assert.Equal(expected.OrderBy(id => id.Value), required.Select(page => page.Id).OrderBy(id => id.Value));
        Assert.DoesNotContain(required, page => page.Id == fixture.Product.Pages[fixture.Product.GetLods(0)[0].FirstPage].Id);
        var stream = Create(scene, fixture.Pages);
        Assert.Equal(2, stream.SourceInstanceCount);
        Assert.Equal(stream.Instances.Span[0].AssetIndex, stream.Instances.Span[1].AssetIndex);
        Assert.Equal(scene.Instances.Span[1].Transform, stream.Instances.Span[1].Transform);
        Assert.Equal(scene.Instances.Span[1].AuthoredId, stream.Instances.Span[1].AuthoredId);
        var tree = Assert.Single(stream.Hierarchies);
        Assert.Equal(fixture.Product.GetLods(0).Length, tree.Nodes.Length);
        Assert.Equal(coarse.IndexCount / 3, tree.Nodes[0].TriangleCount);
        Assert.Equal(fixture.Raster.GetIndices(0).Length / 3, tree.Nodes[^1].TriangleCount);
        Assert.All(stream.ResidentRoots.Values, page => Assert.True(page.TriangleCount > 0));
    }

    [Fact]
    public async Task FinePageUsesTypedDependencyClosureAndPreservesExactVertexChannelsAndIndices()
    {
        var fixture = Input.Value; var scene = Scene(fixture.Product); var acquired = new List<AssetId>();
        var stream = PbrSceneStream.Create(scene, r => fixture.Pages[r.Value],
            (r, _) => Task.FromResult(new MeshRasterPageRecord { Data = fixture.Pages[r.Value] }),
            (r, record) => { acquired.Add(r.Value); return record.Data; });
        var finest = fixture.Product.GetLods(0)[0]; var pageIndex = finest.FirstPage;
        var descriptor = fixture.Product.Pages[pageIndex]; var id = descriptor.Id.Value.ToString("N");
        var prepared = await stream.ReadPreparedPageAsync(id, CancellationToken.None);
        Assert.Empty(acquired);
        Assert.Null(prepared.Virtual); Assert.NotNull(prepared.Raster);
        var retained = prepared.Raster.Sum(record => checked((long)record.Data.PayloadBytes));
        Assert.True(prepared.DecodedBytes >= retained + descriptor.ElementCount * sizeof(uint));
        var geometry = stream.UsePreparedPage(id, prepared);
        Assert.Equal(fixture.Product.GetVertexDependencies(pageIndex).Length + 1, acquired.Count);
        var original = fixture.Raster.GetIndices(0);
        Assert.Equal(descriptor.ElementCount, geometry.Indices.Length);
        for (var index = 0; index < geometry.Indices.Length; index++) {
            var source = (int)original[index]; var compact = (int)geometry.Indices[index];
            Assert.Equal(fixture.Raster.Vertices[source], geometry.GetVertex(compact));
            Assert.Equal(fixture.Raster.BakeUvs[source], geometry.GetBakeUv(compact));
        }
        Assert.True(geometry.VertexCount <= descriptor.ElementCount);
        Assert.True(prepared.DecodedBytes >= retained + geometry.VertexCount * (Unsafe.SizeOf<MeshVertex>() + Unsafe.SizeOf<float2>())
            + geometry.Indices.Length * (sizeof(uint) + 32));
    }

    [Fact]
    public void TransparentBootstrapKeepsExactFinestPagesAndAuthoredBindings()
    {
        var fixture = Input.Value; var scene = Scene(fixture.Product, transparent: true); var stream = Create(scene, fixture.Pages);
        Assert.Empty(stream.Hierarchies); Assert.Empty(stream.Pages); Assert.Empty(stream.ResidentRoots);
        var finest = fixture.Product.GetLods(0)[0];
        Assert.Equal(finest.PageCount * scene.Instances.Length, stream.TransparentInstances.Length);
        for (var instance = 0; instance < scene.Instances.Length; instance++) {
            var original = scene.Instances.Span[instance];
            for (var page = 0; page < finest.PageCount; page++) {
                var actual = stream.TransparentInstances[instance * finest.PageCount + page];
                Assert.Equal(original.Transform, actual.Transform); Assert.Equal(original.AuthoredId, actual.AuthoredId);
                Assert.Equal(original.MaterialSlot, actual.MaterialSlot);
            }
        }
    }

    [Fact]
    public async Task MixedPagesKeepBothTypedChannelsAndShiftDenseDependencies()
    {
        var fixture = Input.Value;
        var source = MeshSource.Create(ProceduralMesh.Sphere(latitudeSegments: 6, longitudeSegments: 12));
        var virtualProduct = MeshVirtualPagesData.Build(source, AssetId.New(),
            MeshPatchBuildSettings.Default with { MaxLeafTriangles = 16 });
        var virtualScene = new PbrVirtualScene([new(virtualProduct.Index, 0)], [new(PbrMaterial.Default)],
            [new(0, 0, float4x4.identity) { AuthoredId = Guid.NewGuid(), MaterialSlot = 0 }], "virtual-control", [3]);
        var mixed = new PbrMixedScene(Scene(fixture.Product), virtualScene, [9, 7, 5]);
        var stream = PbrSceneStream.Create(mixed, r => virtualProduct.Pages[r.Value],
            (r, _) => Task.FromResult(new MeshVirtualPageRecord { Data = virtualProduct.Pages[r.Value] }), (_, record) => record.Data,
            r => fixture.Pages[r.Value], (r, _) => Task.FromResult(new MeshRasterPageRecord { Data = fixture.Pages[r.Value] }),
            (_, record) => record.Data);
        Assert.Equal(3, stream.SourceInstanceCount); Assert.Equal(new byte[] { 9, 7, 5 }, stream.Identity.ToArray());
        Assert.Equal(2, stream.Hierarchies.Length);
        Assert.All(stream.Hierarchies, tree => {
            foreach (var node in tree.Nodes)
                foreach (var part in tree.GetPages(node)) Assert.InRange(part.Page, 0, stream.Pages.Length - 1);
        });
        var rasterDescriptor = fixture.Product.Pages[fixture.Product.GetLods(0)[0].FirstPage];
        var rasterId = rasterDescriptor.Id.Value.ToString("N");
        var rasterPrepared = await stream.ReadPreparedPageAsync(rasterId, CancellationToken.None);
        Assert.NotNull(rasterPrepared.Raster); Assert.Null(rasterPrepared.Virtual);
        Assert.Equal(rasterDescriptor.ElementCount / 3, stream.UsePreparedPage(rasterId, rasterPrepared).TriangleCount);
        var virtualDescriptor = virtualProduct.Index.Pages[0]; var virtualId = virtualDescriptor.Id.Value.ToString("N");
        var virtualPrepared = await stream.ReadPreparedPageAsync(virtualId, CancellationToken.None);
        Assert.NotNull(virtualPrepared.Virtual); Assert.Null(virtualPrepared.Raster);
        Assert.Equal(virtualDescriptor.TriangleCount, stream.UsePreparedPage(virtualId, virtualPrepared).TriangleCount);
    }

    [Fact]
    public async Task CanceledIndexReadDoesNotFetchDependenciesOrAcquireAssets()
    {
        var fixture = Input.Value; var calls = 0; var acquisitions = 0; using var cancellation = new CancellationTokenSource();
        var stream = PbrSceneStream.Create(Scene(fixture.Product), r => fixture.Pages[r.Value],
            (r, _) => { calls++; cancellation.Cancel(); return Task.FromResult(new MeshRasterPageRecord { Data = fixture.Pages[r.Value] }); },
            (_, record) => { acquisitions++; return record.Data; });
        var id = fixture.Product.Pages[fixture.Product.GetLods(0)[0].FirstPage].Id.Value.ToString("N");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadPreparedPageAsync(id, cancellation.Token));
        Assert.Equal(1, calls); Assert.Equal(0, acquisitions);
    }

    [Fact]
    public void PreparationBudgetRejectsBeforeBootstrapAcquisition()
    {
        var fixture = Input.Value; var called = false;
        Assert.Throws<InvalidOperationException>(() => PbrSceneStream.Create(Scene(fixture.Product),
            r => { called = true; return fixture.Pages[r.Value]; },
            (r, _) => Task.FromResult(new MeshRasterPageRecord { Data = fixture.Pages[r.Value] }),
            (_, record) => record.Data, maximumPagePreparationBytes: 1));
        Assert.False(called);
    }

    [Fact]
    public void ChangedVertexDependencyFailsBeforeGeometryPublication()
    {
        var fixture = Input.Value; var page = fixture.Product.GetLods(0)[0].FirstPage;
        var dependencies = fixture.Product.GetVertexDependencies(page).ToArray();
        var vertices = dependencies.ToDictionary(index => index, index => fixture.Pages[fixture.Product.Pages[index].Id]);
        var first = vertices[dependencies[0]].Vertices.ToArray();
        first[0] = first[0] with { UV = new float2(.125f, .25f) };
        vertices[dependencies[0]] = MeshRasterPageData.CreateVertices(first, fixture.Pages[fixture.Product.Pages[dependencies[0]].Id].BakeUvs);
        Assert.Throws<InvalidDataException>(() => StreamGeometryPage.Create(fixture.Product, page,
            fixture.Pages[fixture.Product.Pages[page].Id], vertices));
    }

    private static PbrSceneStream Create(PbrRasterPagesScene scene, IReadOnlyDictionary<AssetId, MeshRasterPageData> pages)
        => PbrSceneStream.Create(scene, r => pages[r.Value],
            (r, _) => Task.FromResult(new MeshRasterPageRecord { Data = pages[r.Value] }), (_, record) => record.Data);

    private static PbrRasterPagesScene Scene(MeshRasterPagesData product, bool transparent = false)
    {
        var first = new PbrSceneInstance(0, 0, float4x4.identity) { AuthoredId = Guid.NewGuid(), MaterialSlot = 0 };
        return new([new(product, 0)], [new(PbrMaterial.Default, AlphaBlend: transparent)],
            [first, first with { AuthoredId = Guid.NewGuid(), Transform = float4x4.TRS(new(3, 2, 1), quaternion.identity, new(2, 1, .5f)) }],
            "exact-raster-page-test", [1, 2, 3], CancellationToken.None);
    }

    private static Fixture CreateFixture()
    {
        var mesh = ProceduralMesh.Sphere(latitudeSegments: 12, longitudeSegments: 24);
        var source = MeshSource.Create(mesh, bakeUvs: mesh.Vertices.Select(vertex => vertex.UV).ToArray());
        var raster = MeshRasterData.Build(source, MeshRasterBuildSettings.Default with { MaximumLods = 3, MinimumTriangles = 16 });
        var leaves = new Dictionary<AssetId, MeshRasterPageData>();
        var product = MeshRasterPagesData.Build(raster, AssetId.New(), AssetId.New(), (id, page) => leaves.Add(id, page));
        Assert.True(product.GetLods(0).Length > 1);
        return new(raster, product, leaves);
    }
}
