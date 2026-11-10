using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    internal sealed record PreparedMixed(AssetId Id, PbrSceneRecord Scene,
        IReadOnlyDictionary<AssetId, MeshManifestRecord> Meshes,
        IReadOnlyDictionary<AssetId, PbrMaterialRecord> Materials,
        IReadOnlyDictionary<AssetId, PbrTextureRecord> Textures,
        IReadOnlyDictionary<AssetId, MeshRasterRecord> Rasters,
        IReadOnlyDictionary<AssetId, MeshVirtualRecord> Virtuals,
        IReadOnlyDictionary<AssetId, MeshBvhRecord> Bvhs,
        IReadOnlyDictionary<Guid, PbrMeshPresentation> Choices,
        IReadOnlyDictionary<AssetId, MeshVirtualPageRecord> Pages, PageSource Source, long MaximumRequiredBytes)
        : Prepared(Id, Scene, Meshes, Materials, Textures), IAsyncDisposable
    {
        internal PbrMixedScene? Composition { get; init; }
        internal PbrBvhScene? TraceComposition { get; init; }
        internal IReadOnlyDictionary<AssetId, MeshRasterPagesRecord> RasterPages { get; init; } = new Dictionary<AssetId, MeshRasterPagesRecord>();
        internal IReadOnlyDictionary<AssetId, MeshRasterPageRecord> RasterBootstrap { get; init; } = new Dictionary<AssetId, MeshRasterPageRecord>();
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }

    internal static async Task<PreparedMixed> PrepareMixedAsync(string path, HttpClient client, RenderQuality quality,
        bool tracing = false, Func<PbrMeshInstance, MeshManifest, PbrMeshPresentation>? select = null,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default, long maximumRequiredBytes = 512L * 1024 * 1024,
        IReadOnlySet<Guid>? rasterInstances = null, AssetPackIndex? packIndex = null)
    {
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRequiredBytes);
        await using var reader = new Reader(path, client, maximumFileBytes, maximumTotalBytes, cancellationToken, packIndex);
        var prepared = await reader.References(); var choices = new Dictionary<Guid, PbrMeshPresentation>();
        var useRasterPages = true;
        foreach (var instance in prepared.Scene.Data.Instances) {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = prepared.Scene.Data.Bindings[instance.Binding]; var mesh = prepared.Meshes[binding.Mesh.Value].Data;
            var transparent = false;
            foreach (var slot in binding.Materials) transparent |= prepared.Materials[slot.Material.Value].Data.AlphaBlend;
            var choice = select is not null ? select(instance, mesh) : quality == RenderQuality.Low || transparent || rasterInstances?.Contains(instance.Id) == true
                || quality == RenderQuality.Medium && mesh.TriangleCount < 2048 ? PbrMeshPresentation.Raster : PbrMeshPresentation.Virtual;
            if (!Enum.IsDefined(choice) || choice == PbrMeshPresentation.Virtual && transparent)
                throw new InvalidAssetException("A selected presentation is invalid or assigns transparent content to Virtual.");
            choices.Add(instance.Id, choice);
            if (choice == PbrMeshPresentation.Raster && mesh.RasterPages is null) useRasterPages = false;
        }
        if (rasterInstances is not null)
            foreach (var id in rasterInstances)
                if (!choices.ContainsKey(id)) throw new ArgumentException("Requested conventional instance is absent from the scene.", nameof(rasterInstances));
        var rasterIds = new List<AssetId>(); var virtualIds = new List<AssetId>(); var bvhIds = new List<AssetId>();
        var uniqueRasters = new HashSet<AssetId>(); var uniqueVirtuals = new HashSet<AssetId>(); var uniqueBvhs = new HashSet<AssetId>();
        for (var i = 0; i < prepared.Scene.Data.Instances.Length; i++) {
            var instance = prepared.Scene.Data.Instances[i];
            cancellationToken.ThrowIfCancellationRequested();
            var mesh = prepared.Meshes[prepared.Scene.Data.Bindings[instance.Binding].Mesh.Value].Data;
            if (choices[instance.Id] == PbrMeshPresentation.Raster) {
                var id = useRasterPages ? mesh.RasterPages!.Id
                    : mesh.Raster?.Id ?? throw new InvalidAssetException("Selected Mesh has no Raster product.");
                if (uniqueRasters.Add(id)) rasterIds.Add(id);
            }
            else {
                var product = mesh.Virtual ?? throw new InvalidAssetException("Selected Mesh has no Virtual product.");
                if (uniqueVirtuals.Add(product.Id)) virtualIds.Add(product.Id);
            }
            if (tracing) {
                var product = mesh.Bvh ?? throw new InvalidAssetException("Selected trace Mesh has no independent BVH product.");
                if (uniqueBvhs.Add(product.Id)) bvhIds.Add(product.Id);
            }
        }
        var rasters = useRasterPages ? new Dictionary<AssetId, MeshRasterRecord>()
            : await reader.ReadMany<MeshRasterRecord>(rasterIds, MeshRasterRecord.Load);
        var rasterPages = useRasterPages ? await reader.ReadMany<MeshRasterPagesRecord>(rasterIds, MeshRasterPagesRecord.Load)
            : new Dictionary<AssetId, MeshRasterPagesRecord>();
        var virtuals = await reader.ReadMany<MeshVirtualRecord>(virtualIds, MeshVirtualRecord.Load);
        var bvhs = await reader.ReadMany<MeshBvhRecord>(bvhIds, MeshBvhRecord.Load);
        var compositionStarted = Stopwatch.GetTimestamp();
        var mixed = useRasterPages ? PbrSceneComposition.ResolveMixedRasterPages(prepared.Scene.Data, i => choices[i.Id], r => prepared.Meshes[r.Value].Data,
            r => rasterPages[r.Value].Data, r => virtuals[r.Value].Data, r => prepared.Materials[r.Value].Data, r => prepared.Textures[r.Value].Data,
            cancellationToken: cancellationToken) : PbrSceneComposition.ResolveMixed(prepared.Scene.Data, i => choices[i.Id], r => prepared.Meshes[r.Value].Data,
            r => rasters[r.Value].Data, r => virtuals[r.Value].Data, r => prepared.Materials[r.Value].Data, r => prepared.Textures[r.Value].Data,
            cancellationToken: cancellationToken);
        var trace = tracing ? PbrSceneComposition.ResolveBvh(prepared.Scene.Data, r => prepared.Meshes[r.Value].Data,
            r => bvhs[r.Value].Data, r => prepared.Materials[r.Value].Data, r => prepared.Textures[r.Value].Data, cancellationToken) : null;
        var compositionMilliseconds = Stopwatch.GetElapsedTime(compositionStarted).TotalMilliseconds;
        var planStarted = Stopwatch.GetTimestamp();
        var required = PbrSceneStream.RequiredPages(mixed.Virtual, maximumRequiredBytes); var total = reader.EncodedBytes;
        var requiredIds = required.Select(page => page.Id).ToHashSet();
        long virtualBytes = 0;
        foreach (var product in virtuals.Values)
            foreach (var page in product.Data.Pages)
                if (requiredIds.Remove(page.Id)) virtualBytes += page.VertexCount * (long)(MeshVertex.Stride + (product.Data.HasBakeUvs ? Unsafe.SizeOf<float2>() : 0))
                    + page.TriangleCount * 12L;
        var rasterRequired = mixed.RasterPages is { } paged
            ? PbrSceneStream.RequiredPages(paged, maximumRequiredBytes - virtualBytes) : [];
        foreach (var page in required) {
            if (page.EncodedBytes > maximumFileBytes || page.EncodedBytes > maximumTotalBytes - total)
                throw new InvalidDataException("Required typed pages exceed the scene encoded admission.");
            total += page.EncodedBytes;
        }
        foreach (var page in rasterRequired) {
            if (page.EncodedBytes > maximumFileBytes || page.EncodedBytes > maximumTotalBytes - total)
                throw new InvalidDataException("Required typed pages exceed the scene encoded admission.");
            total += page.EncodedBytes;
        }
        var planMilliseconds = Stopwatch.GetElapsedTime(planStarted).TotalMilliseconds;
        var statistics = reader.Statistics;
        await reader.DisposeAsync();
        var source = new PageSource(path, client, prepared, virtuals, rasterPages, packIndex: packIndex);
        try {
            var rootsStarted = Stopwatch.GetTimestamp();
            var (records, rasterRecords) = await source.ReadRequiredPagesAsync(required, rasterRequired, cancellationToken);
            var rootsMilliseconds = Stopwatch.GetElapsedTime(rootsStarted).TotalMilliseconds;
            var pages = new Dictionary<AssetId, MeshVirtualPageRecord>(required.Length);
            for (var index = 0; index < required.Length; index++) pages.Add(required[index].Id, records[index]);
            var rasterBootstrap = new Dictionary<AssetId, MeshRasterPageRecord>(rasterRequired.Length);
            for (var index = 0; index < rasterRequired.Length; index++) rasterBootstrap.Add(rasterRequired[index].Id, rasterRecords[index]);
            return new(prepared.Id, prepared.Scene, prepared.Meshes, prepared.Materials, prepared.Textures,
                rasters, virtuals, bvhs, choices, pages, source, maximumRequiredBytes) {
                    EncodedBytes = total, Composition = mixed, TraceComposition = trace,
                    RasterPages = rasterPages, RasterBootstrap = rasterBootstrap,
                    Statistics = statistics with {
                        CompositionMilliseconds = compositionMilliseconds, RequiredPagePlanMilliseconds = planMilliseconds,
                        RequiredPageReadMilliseconds = rootsMilliseconds
                    }
                };
        }
        catch { await source.DisposeAsync(); throw; }
    }

    internal static (Entity Root, PbrSceneStream Stream, PbrBvhScene? Trace) AcquireMixed(World world, PreparedMixed prepared)
    {
        AssetLibrary.RegisterAsset<MeshRasterAsset, MeshRasterRecord>(); AssetLibrary.RegisterAsset<MeshVirtualAsset, MeshVirtualRecord>();
        AssetLibrary.RegisterAsset<MeshBvhAsset, MeshBvhRecord>(); AssetLibrary.RegisterAsset<MeshVirtualPageAsset, MeshVirtualPageRecord>();
        AssetLibrary.RegisterAsset<MeshRasterPagesAsset, MeshRasterPagesRecord>(); AssetLibrary.RegisterAsset<MeshRasterPageAsset, MeshRasterPageRecord>();
        Entity? rasterUse = prepared.RasterBootstrap.Count == 0 ? null : RenderAssetUse.Create(world);
        Entity? acquiredRoot = null;
        try {
            var acquired = Acquire(world, prepared, (w, root) => {
                foreach (var item in prepared.Rasters) w.AcquireAsset(item.Key, () => item.Value, root);
                foreach (var item in prepared.RasterPages) w.AcquireAsset(item.Key, () => item.Value, root);
                foreach (var item in prepared.RasterBootstrap) w.AcquireAsset(item.Key, () => item.Value, rasterUse!.Value);
                foreach (var item in prepared.Virtuals) w.AcquireAsset(item.Key, () => item.Value, root);
                foreach (var item in prepared.Bvhs) w.AcquireAsset(item.Key, () => item.Value, root);
                foreach (var item in prepared.Pages) w.AcquireAsset(item.Key, () => item.Value, root);
            }, (w, scene) => {
                MeshManifest Mesh(AssetRefer<MeshManifestRecord>.Id r) => r.Find(w)?.Get<MeshManifestAsset>().Data ?? throw new AssetNotFoundException();
                PbrMaterialData Material(AssetRefer<PbrMaterialRecord>.Id r) => r.Find(w)?.Get<PbrMaterialResource>().Data ?? throw new AssetNotFoundException();
                PbrTextureData Texture(AssetRefer<PbrTextureRecord>.Id r) => r.Find(w)?.Get<PbrTextureAsset>().Data ?? throw new AssetNotFoundException();
                var sameInputs = UsesPreparedData(w, scene, prepared);
                PbrMixedScene mixed;
                if (sameInputs && prepared.Composition is { } composition) mixed = composition;
                else if (prepared.RasterPages.Count != 0)
                    mixed = PbrSceneComposition.ResolveMixedRasterPages(scene, i => prepared.Choices[i.Id], Mesh,
                        r => r.Find(w)?.Get<MeshRasterPagesAsset>().Data ?? throw new AssetNotFoundException(),
                        r => r.Find(w)?.Get<MeshVirtualAsset>().Data ?? throw new AssetNotFoundException(), Material, Texture);
                else
                    mixed = PbrSceneComposition.ResolveMixed(scene, i => prepared.Choices[i.Id], Mesh,
                        r => r.Find(w)?.Get<MeshRasterAsset>().Data ?? throw new AssetNotFoundException(),
                        r => r.Find(w)?.Get<MeshVirtualAsset>().Data ?? throw new AssetNotFoundException(), Material, Texture);
                var stream = mixed.RasterPages is not null
                    ? PbrSceneStream.Create(mixed, r => r.Find(w)?.Get<MeshVirtualPageAsset>().Data ?? throw new AssetNotFoundException(),
                        prepared.Source.ReadAsync, (reference, record) => AcquirePage(w, reference, record),
                        r => r.Find(w)?.Get<MeshRasterPageAsset>().Data ?? throw new AssetNotFoundException(),
                        prepared.Source.ReadRasterAsync, (reference, record) => AcquireRasterPage(w, reference, record), prepared.MaximumRequiredBytes,
                        textureMips: TextureMips(w, prepared), prepareMip: prepared.Source.ReadMipAsync)
                    : PbrSceneStream.Create(mixed, r => r.Find(w)?.Get<MeshVirtualPageAsset>().Data ?? throw new AssetNotFoundException(),
                        prepared.Source.ReadAsync, (reference, record) => AcquirePage(w, reference, record), prepared.MaximumRequiredBytes,
                        textureMips: TextureMips(w, prepared), prepareMip: prepared.Source.ReadMipAsync);
                PbrBvhScene? trace = null;
                if (prepared.Bvhs.Count != 0)
                    trace = sameInputs && prepared.TraceComposition is { } traceComposition ? traceComposition : PbrSceneComposition.ResolveBvh(scene, Mesh,
                        r => r.Find(w)?.Get<MeshBvhAsset>().Data ?? throw new AssetNotFoundException(), Material, Texture);
                return (Stream: stream, Trace: trace);
            });
            acquiredRoot = acquired.Root;
            if (rasterUse is { } use) { use.Destroy(); rasterUse = null; }
            return (acquired.Root, acquired.Scene.Stream, acquired.Scene.Trace);
        }
        catch (Exception error) {
            var errors = new List<Exception> { error };
            if (acquiredRoot is { } root) {
                try { root.Destroy(); }
                catch (Exception cleanup) { errors.Add(cleanup); }
            }
            if (rasterUse is { } use) {
                try { use.Destroy(); }
                catch (Exception cleanup) { errors.Add(cleanup); }
            }
            if (errors.Count != 1) throw new AggregateException("Mixed scene acquisition cleanup did not complete.", errors);
            throw;
        }
    }

    private static bool UsesPreparedData(World world, PbrSceneData scene, PreparedMixed prepared)
        => ReferenceEquals(scene, prepared.Scene.Data)
            && SameData(world, prepared.Meshes, static r => r.Data, static e => e.Get<MeshManifestAsset>().Data)
            && SameData(world, prepared.Materials, static r => r.Data, static e => e.Get<PbrMaterialResource>().Data)
            && SameData(world, prepared.Textures, static r => r.Data, static e => e.Get<PbrTextureAsset>().Data)
            && SameData(world, prepared.Rasters, static r => r.Data, static e => e.Get<MeshRasterAsset>().Data)
            && SameData(world, prepared.RasterPages, static r => r.Data, static e => e.Get<MeshRasterPagesAsset>().Data)
            && SameData(world, prepared.Virtuals, static r => r.Data, static e => e.Get<MeshVirtualAsset>().Data)
            && SameData(world, prepared.Bvhs, static r => r.Data, static e => e.Get<MeshBvhAsset>().Data);

    private static bool SameData<TRecord, TData>(World world, IReadOnlyDictionary<AssetId, TRecord> records,
        Func<TRecord, TData> prepared, Func<Entity, TData> acquired)
        where TRecord : class, IAssetRecord where TData : class
    {
        var assets = world.AcquireAddon<AssetLibrary>();
        foreach (var pair in records)
            if (!assets.TryGet<TRecord>(pair.Key, out var entity) || !ReferenceEquals(prepared(pair.Value), acquired(entity))) return false;
        return true;
    }
}
