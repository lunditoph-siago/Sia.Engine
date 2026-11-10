using System.Net;
using Sia;
using Sia.Asset;
using Sia.Engine.Example;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class ScenePackedPreparationTests
{
    [Fact]
    public async Task PackedPreparationConsumesEachPhysicalGroupBeforeChangingPacks()
    {
        var fixture = CreateFixture();
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(fixture.Response(request))));

        var prepared = await PbrSceneFiles.PrepareRasterAsync(fixture.ScenePath, client, packIndex: fixture.Index);

        Assert.Equal(fixture.Meshes, prepared.Meshes.Keys);
        Assert.Equal(fixture.Rasters, prepared.Products.Keys);
        Assert.Equal(4, prepared.Statistics.MetadataCache.ReadAttempts);
        Assert.Equal(fixture.LogicalBytes, prepared.EncodedBytes);
        Assert.True(prepared.Statistics.MetadataCache.PeakReservedBytes <= AssetChunk.MaximumLength + 1L);
        Assert.All(prepared.Products.Values, product => Assert.Equal(fixture.SourceHash, product.Data.SourceHash));
    }

    [Fact]
    public async Task GroupedPreparationStillAuthenticatesEveryLeafInsideAnAuthenticatedPack()
    {
        var fixture = CreateFixture(corruptManifest: true);
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(fixture.Response(request))));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            PbrSceneFiles.PrepareRasterAsync(fixture.ScenePath, client, packIndex: fixture.Index));

        Assert.Equal("Packed leaf checksum mismatch.", error.Message);
    }

    [Fact]
    public async Task CancelledPackedPreparationJoinsTheBlockedPhysicalRead()
    {
        var fixture = CreateFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = false;
        using var client = new HttpClient(new Handler(async (request, token) => {
            if (Path.GetFileName(request.RequestUri!.AbsolutePath) != fixture.SecondPack) return fixture.Response(request);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); }
            finally { joined = true; }
        }));
        using var cancellation = new CancellationTokenSource();
        var preparing = PbrSceneFiles.PrepareRasterAsync(fixture.ScenePath, client, cancellationToken: cancellation.Token, packIndex: fixture.Index);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(joined);
    }

    [Fact]
    public async Task PreparedCompositionRespectsAnAlreadyAcquiredMaterialRecord()
    {
        var fixture = CreateFixture();
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(fixture.Response(request))));
        await using var prepared = await PbrSceneFiles.PrepareMixedAsync(fixture.ScenePath, client, RenderQuality.Low, packIndex: fixture.Index);
        var replacement = new PbrMaterialRecord { Data = new(PbrMaterial.Default with { BaseColor = new(0.8f, 0.1f, 0.2f) }) };
        using var world = new World();
        world.AcquireAddon<AssetLibrary>();
        AssetLibrary.RegisterAsset<PbrMaterialResource, PbrMaterialRecord>();
        world.AcquireAsset(fixture.Material, () => replacement);

        var acquired = PbrSceneFiles.AcquireMixed(world, prepared);

        Assert.Equal(replacement.Data.Parameters, Assert.Single(acquired.Stream.Materials.ToArray()).Parameters);
        Assert.NotEqual(replacement.Data.Parameters, Assert.Single(prepared.Composition!.Raster.Materials.ToArray()).Parameters);
        acquired.Root.Destroy();
    }

    [Fact]
    public async Task MixedPreparationUsesPagedRasterMetadataAndAcquiresItsExactLeaves()
    {
        var fixture = CreateFixture(pagedRaster: true);
        using var client = new HttpClient(new Handler((request, _) => {
            return Task.FromResult(fixture.Response(request));
        }));
        await using var prepared = await PbrSceneFiles.PrepareMixedAsync(fixture.ScenePath, client, RenderQuality.Medium, packIndex: fixture.Index);

        Assert.Empty(prepared.Rasters);
        Assert.Equal(3, prepared.RasterPages.Count);
        Assert.NotEmpty(prepared.RasterBootstrap);
        Assert.NotNull(prepared.Composition!.RasterPages);
        using var world = new World();
        world.AcquireAddon<AssetLibrary>();
        var acquired = PbrSceneFiles.AcquireMixed(world, prepared);
        Assert.Equal(3, acquired.Stream.SourceInstanceCount);
        Assert.NotEmpty(acquired.Stream.ResidentRoots);
        Assert.All(acquired.Stream.ResidentRoots.Values, page => Assert.Equal(12, page.TriangleCount));
        acquired.Root.Destroy();
        Assert.Empty(world.GetAddon<AssetLibrary>().Entities);
    }

    [Fact]
    public async Task LowKeepsWholeRasterProductsWhenNewManifestsAlsoDeclarePages()
    {
        var fixture = CreateFixture(pagedRaster: true);
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(fixture.Response(request))));
        await using var prepared = await PbrSceneFiles.PrepareRasterStreamingAsync(fixture.ScenePath, client, packIndex: fixture.Index);

        Assert.Equal(fixture.Rasters, prepared.Index.Products.Keys);
        Assert.Equal(fixture.LogicalBytes, prepared.EncodedBytes);
        Assert.All(prepared.Meshes.Values, mesh => Assert.NotNull(mesh.Data.RasterPages));
        using var world = new World();
        world.AcquireAddon<AssetLibrary>();
        var acquired = PbrSceneFiles.AcquireRasterStreaming(world, prepared);
        Assert.Null(acquired.Stream);
        Assert.Equal(3, acquired.Scene.Instances.Length);
        acquired.Root.Destroy();
        Assert.Empty(world.GetAddon<AssetLibrary>().Entities);
    }

    [Fact]
    public async Task RequiredBootstrapConsumesPhysicalGroupsAndRestoresInterleavedLeafOrder()
    {
        var (fixture, _) = CreatePackedBootstrapFixture();
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(fixture.Response(request))));
        await using var prepared = await PbrSceneFiles.PrepareRasterPagesStreamingAsync(fixture.ScenePath, client, packIndex: fixture.Index);
        var products = prepared.Index.Products.Values.ToArray();
        var first = products[0].Data.Pages.ToArray().First(page => page.Kind == MeshRasterPageKind.Vertices);
        var middle = products[0].Data.Pages.ToArray().First(page => page.Kind == MeshRasterPageKind.Indices);
        var last = products[1].Data.Pages.ToArray().First(page => page.Kind == MeshRasterPageKind.Vertices);
        MeshRasterPageDescriptor[] required = [first, middle, last];
        var budget = AssetChunk.MaximumLength + 1L;
        await using var source = new PbrSceneFiles.PageSource(fixture.ScenePath, client, prepared.Index,
            cacheBytes: budget, packIndex: fixture.Index);

        var (virtuals, rasters) = await source.ReadRequiredPagesAsync([], required, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(virtuals);
        Assert.Equal(required.Select(page => page.ContentHash), rasters.Select(page => page.Data.ContentHash));
        Assert.Equal(2, source.Statistics.ReadAttempts);
        Assert.Equal(2, source.BootstrapDecodeGroups);
        Assert.Equal(3, source.BootstrapDecodeLeaves);
        Assert.True(source.Statistics.PeakReservedBytes <= budget);
        Assert.True(source.Statistics.Evictions > 0);
    }

    [Fact]
    public async Task MixedBootstrapUsesGroupedPhysicalPacksAndPreservesExactTypedLeaves()
    {
        var (fixture, _) = CreatePackedBootstrapFixture();
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(fixture.Response(request))));
        await using var prepared = await PbrSceneFiles.PrepareMixedAsync(fixture.ScenePath, client, RenderQuality.Medium, packIndex: fixture.Index);

        Assert.NotEmpty(prepared.RasterBootstrap);
        Assert.Equal(2, prepared.Source.BootstrapDecodeGroups);
        Assert.Equal(prepared.RasterBootstrap.Count, prepared.Source.BootstrapDecodeLeaves);
        Assert.Equal(2, prepared.Source.Statistics.ReadAttempts);
        using var world = new World();
        world.AcquireAddon<AssetLibrary>();
        var acquired = PbrSceneFiles.AcquireMixed(world, prepared);
        Assert.Equal(3, acquired.Stream.SourceInstanceCount);
        acquired.Root.Destroy();
        Assert.Empty(world.GetAddon<AssetLibrary>().Entities);
    }

    [Fact]
    public async Task VirtualAndRasterBootstrapShareOnePhysicalLeaseAndDecodeBatch()
    {
        var fixture = CreateFixture(pagedRaster: true);
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(fixture.Response(request))));
        await using var prepared = await PbrSceneFiles.PrepareRasterPagesStreamingAsync(fixture.ScenePath, client, packIndex: fixture.Index);
        var mesh = ProceduralMesh.Cube();
        var sourceMesh = MeshSource.Create(mesh, [new(0, mesh.Indices.Length, 0)]);
        var virtualId = AssetId.New();
        var virtualProduct = MeshVirtualPagesData.Build(sourceMesh, virtualId);
        var virtualRecords = new Dictionary<AssetId, MeshVirtualRecord> { [virtualId] = new() { Data = virtualProduct.Index } };
        var raster = prepared.Index.Products.Values.First().Data.Pages.ToArray().First(page => page.Kind == MeshRasterPageKind.Indices);
        var entries = new List<AssetPackEntry>();
        var slices = new List<(string Name, AssetChunk Chunk, int Offset)>();
        using var physicalBytes = new MemoryStream();
        foreach (var page in virtualProduct.Pages) {
            using var encoded = new MemoryStream(); page.Value.Save(encoded);
            var bytes = encoded.ToArray();
            slices.Add((page.Key.Value.ToString("N") + ".siaasset", AssetChunk.FromBytes(bytes), (int)physicalBytes.Length));
            physicalBytes.Write(bytes);
        }
        var rasterFile = raster.Id.Value.ToString("N") + ".siaasset";
        var rasterBytes = fixture.Packs[rasterFile];
        slices.Add((rasterFile, AssetChunk.FromBytes(rasterBytes), (int)physicalBytes.Length));
        physicalBytes.Write(rasterBytes);
        var pack = AssetChunk.FromBytes(physicalBytes.ToArray());
        fixture.Packs.Add(pack.FileName, physicalBytes.ToArray());
        foreach (var slice in slices) entries.Add(new(slice.Name, slice.Chunk, new(pack, slice.Offset)));
        await using var source = new PbrSceneFiles.PageSource(fixture.ScenePath, client, prepared.Index,
            virtualRecords, prepared.Index.Products, packIndex: new AssetPackIndex(entries));

        var (virtualPages, rasterPages) = await source.ReadRequiredPagesAsync(virtualProduct.Index.Pages.ToArray(), [raster], CancellationToken.None);

        Assert.Equal(virtualProduct.Index.Pages.ToArray().Select(page => page.ContentHash), virtualPages.Select(page => page.Data.ContentHash));
        Assert.Equal(raster.ContentHash, Assert.Single(rasterPages).Data.ContentHash);
        Assert.Equal(1, source.Statistics.ReadAttempts);
        Assert.Equal(1, source.BootstrapDecodeGroups);
        Assert.Equal(virtualPages.Length + 1, source.BootstrapDecodeLeaves);
    }

    [Fact]
    public async Task GroupedRequiredBootstrapStillRejectsBadLeafInsideAuthenticatedPhysicalPack()
    {
        var (fixture, _) = CreatePackedBootstrapFixture(corruptLeaf: true);
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(fixture.Response(request))));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            PbrSceneFiles.PrepareMixedAsync(fixture.ScenePath, client, RenderQuality.Medium, packIndex: fixture.Index));

        Assert.Equal("Packed leaf checksum mismatch.", error.Message);
    }

    [Fact]
    public async Task CancelledGroupedRequiredBootstrapJoinsItsBlockedPhysicalRead()
    {
        var (fixture, blockedPack) = CreatePackedBootstrapFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = false;
        using var client = new HttpClient(new Handler(async (request, token) => {
            if (Path.GetFileName(request.RequestUri!.AbsolutePath) != blockedPack) return fixture.Response(request);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); }
            finally { joined = true; }
        }));
        using var cancellation = new CancellationTokenSource();
        var preparing = PbrSceneFiles.PrepareMixedAsync(fixture.ScenePath, client, RenderQuality.Medium,
            cancellationToken: cancellation.Token, packIndex: fixture.Index);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(joined);
    }

    private static (Fixture Fixture, string SecondPack) CreatePackedBootstrapFixture(bool corruptLeaf = false)
    {
        var fixture = CreateFixture(pagedRaster: true);
        var entries = fixture.Index.Entries.Values.ToList();
        string Pack(bool vertices)
        {
            var bytes = new byte[8 * 1024 * 1024 + 1];
            var offset = 0;
            var byHash = new Dictionary<string, int>(StringComparer.Ordinal);
            var leaves = new List<(string Name, AssetChunk Chunk, int Offset)>();
            foreach (var file in fixture.Packs.Where(pair => pair.Key.EndsWith(".siaasset", StringComparison.Ordinal)
                && pair.Value.AsSpan().StartsWith("SIAMSHRP"u8))) {
                var kind = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(file.Value.AsSpan(8));
                if ((kind == (int)MeshRasterPageKind.Vertices) != vertices) continue;
                var chunk = AssetChunk.FromBytes(file.Value);
                if (!byHash.TryGetValue(chunk.Id, out var start)) {
                    start = offset; byHash.Add(chunk.Id, start);
                    file.Value.CopyTo(bytes, start); offset += file.Value.Length;
                }
                leaves.Add((file.Key, chunk, start));
            }
            if (corruptLeaf && vertices) bytes[8] ^= 1;
            var physical = AssetChunk.FromBytes(bytes);
            fixture.Packs.Add(physical.FileName, bytes);
            foreach (var leaf in leaves) entries.Add(new(leaf.Name, leaf.Chunk, new(physical, leaf.Offset)));
            return physical.FileName;
        }
        Pack(vertices: true);
        var second = Pack(vertices: false);
        return (fixture with { Index = new AssetPackIndex(entries) }, second);
    }

    private sealed record Fixture(string ScenePath, AssetPackIndex Index, AssetId[] Meshes, AssetId[] Rasters,
        AssetId Material, string SourceHash, long LogicalBytes, string SecondPack, Dictionary<string, byte[]> Packs)
    {
        public HttpResponseMessage Response(HttpRequestMessage request)
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Packs[name]) };
        }
    }

    private static Fixture CreateFixture(bool corruptManifest = false, bool pagedRaster = false)
    {
        var mesh = ProceduralMesh.Cube();
        var source = MeshSource.Create(mesh, [new(0, mesh.Indices.Length, 0)]);
        var rasters = Enumerable.Range(0, 3).Select(i => MeshRasterData.Build(source,
            MeshRasterBuildSettings.Default with { MaximumLods = i + 1 })).ToArray();
        var sourceId = AssetId.New(); var sceneId = AssetId.New(); var materialId = AssetId.New();
        var meshIds = Enumerable.Range(0, 3).Select(_ => AssetId.New()).ToArray();
        var rasterIds = Enumerable.Range(0, 3).Select(_ => AssetId.New()).ToArray();
        var pageIndexIds = Enumerable.Range(0, 3).Select(_ => AssetId.New()).ToArray();
        var loosePages = new Dictionary<string, byte[]>();
        var bindings = meshIds.Select(id => new PbrMeshBinding(new(id), [new(0, new(materialId))])).ToArray();
        var instances = Enumerable.Range(0, 3).Select(i => new PbrMeshInstance(Guid.NewGuid(), i, float4x4.identity)).ToArray();
        var scene = PbrSceneData.Create(bindings, instances);
        var bodies = new Dictionary<string, byte[]>();
        static byte[] Encode(Action<Stream> save) { using var output = new MemoryStream(); save(output); return output.ToArray(); }
        static string Name(AssetId id) => id.Value.ToString("N") + ".siaasset";
        var sceneFile = sceneId.Value.ToString("N") + ".siascene";
        bodies.Add(sceneFile, Encode(stream => scene.Save(stream)));
        bodies.Add(Name(materialId), Encode(stream => new PbrMaterialData(PbrMaterial.Default).Save(stream)));
        for (var i = 0; i < meshIds.Length; i++) {
            var raster = rasters[i];
            var manifest = MeshManifest.Create(sourceId, source, new(rasterIds[i], raster.BuildKey));
            if (pagedRaster) {
                var index = MeshRasterPagesData.Build(raster, rasterIds[i], pageIndexIds[i],
                    (id, page) => loosePages.Add(Name(id), Encode(stream => page.Save(stream))));
                manifest = manifest.WithRasterPages(new(pageIndexIds[i], index.BuildKey));
                bodies.Add(Name(pageIndexIds[i]), Encode(stream => index.Save(stream)));
            }
            bodies.Add(Name(meshIds[i]), Encode(manifest.Save));
            bodies.Add(Name(rasterIds[i]), Encode(stream => raster.Save(stream)));
        }
        var entries = new List<AssetPackEntry>(); var packs = new Dictionary<string, byte[]>();
        string Pack(string[] files)
        {
            var bytes = new byte[8 * 1024 * 1024 + 1]; var offset = 0;
            var leaves = new List<(string Name, AssetChunk Chunk, int Offset)>();
            foreach (var file in files) {
                var body = bodies[file]; var chunk = AssetChunk.FromBytes(body);
                body.CopyTo(bytes, offset);
                if (corruptManifest && file == Name(meshIds[0])) bytes[offset + 8] ^= 1;
                leaves.Add((file, chunk, offset)); offset += body.Length;
            }
            var pack = AssetChunk.FromBytes(bytes); packs.Add(pack.FileName, bytes);
            foreach (var leaf in leaves) entries.Add(new(leaf.Name, leaf.Chunk, new(pack, leaf.Offset)));
            return pack.FileName;
        }
        var products = pagedRaster ? pageIndexIds : rasterIds;
        Pack([sceneFile, Name(materialId), Name(meshIds[0]), Name(meshIds[2]), Name(products[0]), Name(products[2])]);
        var secondPack = Pack([Name(meshIds[1]), Name(products[1])]);
        foreach (var page in loosePages) packs.Add(page.Key, page.Value);
        if (pagedRaster)
            foreach (var id in rasterIds) packs.Add(Name(id), bodies[Name(id)]);
        return new("https://assets.invalid/" + sceneFile, new(entries), meshIds, rasterIds, materialId, source.ContentHash,
            bodies.Where(pair => !pageIndexIds.Any(id => Name(id) == pair.Key)).Sum(pair => (long)pair.Value.Length), secondPack, packs);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
