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
        ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumFileBytes); ArgumentOutOfRangeException.ThrowIfNegative(maximumTotalBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var http = AssetInput.TryGetHttpUri(path, out var uri);
        var filename = Path.GetFileNameWithoutExtension(http ? uri!.AbsolutePath : path);
        if (!IsReferenceScene(path) || !Guid.TryParseExact(filename, "N", out var guid) || guid == Guid.Empty)
            throw new ArgumentException("Reference scenes require a nonempty Guid in N format and .siascene suffix.", nameof(path));
        var root = http ? null : Path.GetDirectoryName(Path.GetFullPath(path))!;
        string Locate(AssetId id) => http ? new Uri(uri!, id.Value.ToString("N") + ".siaasset").AbsoluteUri
            : Path.Combine(root!, id.Value.ToString("N") + ".siaasset");
        var claims = new Dictionary<AssetId, Type>(); long total = 0;

        async Task<T> Read<T>(AssetId id, string location, Func<Stream, string, T> decode) where T : class, IAssetRecord
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (claims.TryGetValue(id, out var kind) && kind != typeof(T))
                throw new InvalidDataException("One logical asset ID cannot identify different record kinds.");
            claims[id] = typeof(T);
            using var input = await AssetInput.OpenAsync(location, client, cancellationToken);
            var limit = (int)System.Math.Min(maximumFileBytes, maximumTotalBytes - total);
            var bytes = await AssetInput.ReadBoundedAsync(input, limit, cancellationToken);
            total += bytes.Length;
            cancellationToken.ThrowIfCancellationRequested();
            T Decode(CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
                if (!MemoryMarshal.TryGetArray(bytes, out var segment)) throw new InvalidOperationException("Expected owned encoded array.");
                using var encoded = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
                return decode(encoded, location);
            }
#if BROWSER
            return await Program.BrowserOwner.RunCpuAsync(Decode, cancellationToken);
#else
            return Decode(cancellationToken);
#endif
        }

        var id = new AssetId(guid);
        var scene = await Read(id, path, (s, n) => PbrSceneRecord.Load(s, (ulong)maximumFileBytes, n));
        var meshes = new Dictionary<AssetId, MeshManifestRecord>(); var products = new Dictionary<AssetId, TProduct>();
        var materials = new Dictionary<AssetId, PbrMaterialRecord>(); var textures = new Dictionary<AssetId, PbrTextureRecord>();
        var uses = new int[scene.Data.Bindings.Length];
        for (var i = 0; i < scene.Data.Instances.Length; i++) uses[scene.Data.Instances[i].Binding]++;
        long expanded = 0; var geometryCount = 0;
        for (var i = 0; i < uses.Length; i++) {
            if (uses[i] == 0) continue;
            var binding = scene.Data.Bindings[i];
            if (!meshes.TryGetValue(binding.Mesh.Value, out var mesh)) {
                mesh = await Read<MeshManifestRecord>(binding.Mesh.Value, Locate(binding.Mesh.Value), MeshManifestRecord.Load);
                meshes.Add(binding.Mesh.Value, mesh);
            }
            var selected = selectProduct(mesh.Data);
            if (!products.TryGetValue(selected, out var product)) {
                product = await Read(selected, Locate(selected), (s, n) => decodeProduct(s, (ulong)maximumFileBytes, n));
                products.Add(selected, product);
                geometryCount += partCount(product);
                if (geometryCount > 4096) throw new InvalidDataException("Resolved geometry table limit exceeded.");
            }
            expanded += (long)uses[i] * partCount(product);
            if (expanded > PbrSceneData.MaximumInstances) throw new InvalidDataException("Expanded scene instance limit exceeded.");
            for (var slotIndex = 0; slotIndex < binding.Materials.Length; slotIndex++) {
                var slot = binding.Materials[slotIndex];
                if (materials.ContainsKey(slot.Material.Value)) continue;
                if (materials.Count == 4096) throw new InvalidDataException("Resolved material table limit exceeded.");
                var material = await Read<PbrMaterialRecord>(slot.Material.Value, Locate(slot.Material.Value), PbrMaterialRecord.Load);
                materials.Add(slot.Material.Value, material);
                foreach (var reference in material.Data.TextureReferences) {
                    if (textures.ContainsKey(reference.Value)) continue;
                    if (textures.Count == 4096) throw new InvalidDataException("Resolved texture table limit exceeded.");
                    var texture = await Read(reference.Value, Locate(reference.Value), (s, n) => PbrTextureRecord.Load(s, (ulong)maximumFileBytes, n));
                    textures.Add(reference.Value, texture);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(id, scene, meshes, products, materials, textures) { EncodedBytes = total };
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
            foreach (var item in prepared.Products) world.AcquireAsset(item.Key, () => item.Value, root);
            foreach (var item in prepared.Materials) world.AcquireAsset(item.Key, () => item.Value, root);
            foreach (var item in prepared.Textures) world.AcquireAsset(item.Key, () => item.Value, root);
            var resolved = compose(world, scene.Get<PbrSceneResource>().Data);
            return (root, resolved);
        }
        catch { root.Destroy(); throw; }
    }
}
