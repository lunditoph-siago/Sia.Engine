using System.Runtime.InteropServices;
using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

// Application file layout and workflow, deliberately outside the asset library.
internal static partial class PbrSceneFiles
{
    internal abstract record Prepared(AssetId Id, PbrSceneRecord Scene,
        IReadOnlyDictionary<AssetId, MeshManifestRecord> Meshes,
        IReadOnlyDictionary<AssetId, PbrMaterialRecord> Materials,
        IReadOnlyDictionary<AssetId, PbrTextureRecord> Textures)
    {
        internal long EncodedBytes { get; init; }
    }

    internal sealed record Prepared<TProduct>(AssetId Id, PbrSceneRecord Scene,
        IReadOnlyDictionary<AssetId, MeshManifestRecord> Meshes, IReadOnlyDictionary<AssetId, TProduct> Products,
        IReadOnlyDictionary<AssetId, PbrMaterialRecord> Materials, IReadOnlyDictionary<AssetId, PbrTextureRecord> Textures)
        : Prepared(Id, Scene, Meshes, Materials, Textures) where TProduct : class, IAssetRecord;

    internal static bool IsReferenceScene(string path)
        => (AssetInput.TryGetHttpUri(path, out var uri) ? uri!.AbsolutePath : path)
            .EndsWith(".siascene", StringComparison.OrdinalIgnoreCase);

    internal static Task<Prepared<MeshVirtualRecord>> PrepareVirtualAsync(string path, HttpClient client,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default)
        => PrepareAsync(path, client, static m => m.Virtual?.Id ?? throw new InvalidDataException("Selected Mesh has no Virtual product."),
            static (MeshVirtualRecord r) => r.Data.Parts.Length, MeshVirtualRecord.Load,
            maximumFileBytes, maximumTotalBytes, cancellationToken);

    internal static Task<Prepared<MeshRasterRecord>> PrepareRasterAsync(string path, HttpClient client,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default)
        => PrepareAsync(path, client, static m => m.Raster?.Id ?? throw new InvalidDataException("Selected Mesh has no Raster product."),
            static (MeshRasterRecord r) => r.Data.Sections.Length, MeshRasterRecord.Load,
            maximumFileBytes, maximumTotalBytes, cancellationToken);

    private static async Task<Prepared<TProduct>> PrepareAsync<TProduct>(string path, HttpClient client,
        Func<MeshManifest, AssetId> selectProduct, Func<TProduct, int> partCount, Func<Stream, ulong, string?, TProduct> decodeProduct,
        int maximumFileBytes, long maximumTotalBytes, CancellationToken cancellationToken) where TProduct : class, IAssetRecord
    {
        var reader = new Reader(path, client, maximumFileBytes, maximumTotalBytes, cancellationToken);
        var prepared = await reader.References(); var scene = prepared.Scene;
        var meshes = prepared.Meshes; var products = new Dictionary<AssetId, TProduct>();
        var uses = new int[scene.Data.Bindings.Length];
        for (var i = 0; i < scene.Data.Instances.Length; i++) uses[scene.Data.Instances[i].Binding]++;
        long expanded = 0; var geometryCount = 0;
        for (var i = 0; i < uses.Length; i++) {
            if (uses[i] == 0) continue;
            var binding = scene.Data.Bindings[i];
            var mesh = meshes[binding.Mesh.Value];
            var selected = selectProduct(mesh.Data);
            if (!products.TryGetValue(selected, out var product)) {
                product = await reader.Read(selected, decodeProduct);
                products.Add(selected, product);
                geometryCount += partCount(product);
                if (geometryCount > 4096) throw new InvalidDataException("Resolved geometry table limit exceeded.");
            }
            expanded += (long)uses[i] * partCount(product);
            if (expanded > PbrSceneData.MaximumInstances) throw new InvalidDataException("Expanded scene instance limit exceeded.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(prepared.Id, scene, meshes, products, prepared.Materials, prepared.Textures) { EncodedBytes = reader.EncodedBytes };
    }

    // Must run synchronously on the target World's owner context after preparation.
    // Each use owns an anonymous root; scene identity and selected assets can be shared.
    internal static (Entity Root, PbrVirtualScene Scene) AcquireVirtual(World world, Prepared<MeshVirtualRecord> prepared)
    {
        AssetLibrary.RegisterAsset<MeshVirtualAsset, MeshVirtualRecord>();
        return Acquire<MeshVirtualRecord, PbrVirtualScene>(world, prepared, static (w, s) => {
            var scene = PbrSceneComposition.ResolveVirtual(s,
                r => r.Find(w)?.Get<MeshManifestAsset>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<MeshVirtualAsset>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<PbrMaterialResource>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<PbrTextureAsset>().Data ?? throw new AssetNotFoundException());
            return scene;
        });
    }

    internal static (Entity Root, PbrRasterScene Scene) AcquireRaster(World world, Prepared<MeshRasterRecord> prepared)
    {
        AssetLibrary.RegisterAsset<MeshRasterAsset, MeshRasterRecord>();
        return Acquire<MeshRasterRecord, PbrRasterScene>(world, prepared, static (w, s) => {
            var scene = PbrSceneComposition.ResolveRaster(s,
                r => r.Find(w)?.Get<MeshManifestAsset>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<MeshRasterAsset>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<PbrMaterialResource>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<PbrTextureAsset>().Data ?? throw new AssetNotFoundException());
            return scene;
        });
    }

    private static (Entity Root, TScene Scene) Acquire<TProduct, TScene>(World world, Prepared<TProduct> prepared,
        Func<World, PbrSceneData, TScene> compose) where TProduct : class, IAssetRecord
        => Acquire(world, prepared, (w, root) => {
            foreach (var item in prepared.Products) w.AcquireAsset(item.Key, () => item.Value, root);
        }, compose);

    private static (Entity Root, TScene Scene) Acquire<TScene>(World world, Prepared prepared,
        Action<World, Entity> acquireProducts, Func<World, PbrSceneData, TScene> compose)
    {
        ArgumentNullException.ThrowIfNull(world); ArgumentNullException.ThrowIfNull(prepared);
        AssetLibrary.RegisterAsset<PbrSceneResource, PbrSceneRecord>();
        AssetLibrary.RegisterAsset<MeshManifestAsset, MeshManifestRecord>();
        AssetLibrary.RegisterAsset<PbrMaterialResource, PbrMaterialRecord>();
        AssetLibrary.RegisterAsset<PbrTextureAsset, PbrTextureRecord>();
        var root = RenderAssetUse.Create(world, new(prepared.Id));
        try {
            var scene = world.AcquireAsset(prepared.Id, () => prepared.Scene, root);
            foreach (var item in prepared.Meshes) world.AcquireAsset(item.Key, () => item.Value, root);
            acquireProducts(world, root);
            foreach (var item in prepared.Materials) world.AcquireAsset(item.Key, () => item.Value, root);
            foreach (var item in prepared.Textures) world.AcquireAsset(item.Key, () => item.Value, root);
            var resolved = compose(world, scene.Get<PbrSceneResource>().Data);
            return (root, resolved);
        }
        catch { root.Destroy(); throw; }
    }

    internal static Task<Prepared<MeshBvhRecord>> PrepareBvhAsync(string path, HttpClient client,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default)
        => PrepareAsync(path, client, static m => m.Bvh?.Id ?? throw new InvalidDataException("Selected Mesh has no BVH product."),
            static (MeshBvhRecord _) => 1, MeshBvhRecord.Load,
            maximumFileBytes, maximumTotalBytes, cancellationToken);

    internal static (Entity Root, PbrBvhScene Scene) AcquireBvh(World world, Prepared<MeshBvhRecord> prepared)
    {
        AssetLibrary.RegisterAsset<MeshBvhAsset, MeshBvhRecord>();
        return Acquire<MeshBvhRecord, PbrBvhScene>(world, prepared, static (w, s) => {
            var scene = PbrSceneComposition.ResolveBvh(s,
                r => r.Find(w)?.Get<MeshManifestAsset>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<MeshBvhAsset>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<PbrMaterialResource>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<PbrTextureAsset>().Data ?? throw new AssetNotFoundException());
            return scene;
        });
    }
}
