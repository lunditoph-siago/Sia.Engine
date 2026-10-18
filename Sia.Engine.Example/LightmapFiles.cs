using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sia;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

// Application-owned location, transport, validation and original World composition.
internal static partial class LightmapFiles
{
    internal sealed record Prepared(AssetId Id, PbrLightmapSetRecord Set, ProductSource Source) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }

    internal static async Task<Prepared> PrepareAsync(string path, HttpClient http, PbrSceneFiles.Prepared scene,
        ulong maximumMetadataBytes = PbrLightmapSetData.MaximumPayloadBytes, long cacheBytes = 32L * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheBytes, 2); cancellationToken.ThrowIfCancellationRequested();
        var remote = AssetInput.TryGetHttpUri(path, out var uri); var filename = remote ? uri!.AbsolutePath : path;
        if (!filename.EndsWith(".siaasset", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(filename), "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("Lighting set requires its nonempty UUID and .siaasset suffix.", nameof(path));
        ReadOnlyMemory<byte> bytes;
        using (var input = await AssetInput.OpenAsync(path, http, cancellationToken))
            bytes = await AssetInput.ReadBoundedAsync(input, (int)System.Math.Min(maximumMetadataBytes, PbrLightmapSetData.MaximumEncodedBytes), cancellationToken);
        if (!MemoryMarshal.TryGetArray(bytes, out var encoded)) throw new InvalidOperationException("Expected array-backed lighting input.");
        PbrLightmapSetRecord Decode(CancellationToken token) {
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(encoded.Array!, encoded.Offset, encoded.Count, writable: false);
            return PbrLightmapSetRecord.Load(stream, maximumMetadataBytes, path);
        }
#if BROWSER
        var set = await Program.BrowserOwner.RunCpuAsync(Decode, cancellationToken);
#else
        var set = Decode(cancellationToken);
#endif
        if (set.Data.ProductId.Value != id) throw new InvalidDataException("Lighting set belongs to another logical ID.");
        set.Data.ValidateScene(scene.Scene.Data, r => scene.Meshes[r.Value].Data, r => scene.Materials[r.Value].Data,
            r => scene.Textures[r.Value].Data, cancellationToken);
        var ids = new HashSet<AssetId>(scene.Meshes.Keys.Concat(scene.Materials.Keys).Concat(scene.Textures.Keys).Append(scene.Id));
        foreach (var mesh in scene.Meshes.Values) {
            ids.Add(mesh.Data.Source);
            if (mesh.Data.Raster is { } raster) ids.Add(raster.Id);
            if (mesh.Data.Bvh is { } bvh) ids.Add(bvh.Id);
            if (mesh.Data.Virtual is { } virtualData) ids.Add(virtualData.Id);
        }
        var virtuals = scene switch {
            PbrSceneFiles.PreparedStreaming streaming => streaming.Index.Products.Values,
            PbrSceneFiles.Prepared<Sia.Engine.Mesh.MeshVirtualRecord> prepared => prepared.Products.Values,
            _ => Enumerable.Empty<Sia.Engine.Mesh.MeshVirtualRecord>()
        };
        foreach (var product in virtuals) foreach (var page in product.Data.Pages) ids.Add(page.Id);
        cancellationToken.ThrowIfCancellationRequested();
        return new(new(id), set, new(path, http, set.Data, cacheBytes, otherIds: ids));
    }

    internal static Entity Acquire(World world, Prepared prepared, PbrSceneData scene, Entity owner)
    {
        RequireOwner(world, owner); ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Id != prepared.Set.Data.ProductId) throw new InvalidAssetException("Prepared lighting identity differs from its set.");
        return AcquireSet(world, prepared.Source, prepared.Set, scene, owner);
    }

    internal static PbrLightmapInput CreateInput(World world, Prepared prepared, PbrSceneData scene, Entity owner)
    {
        var set = Acquire(world, prepared, scene, owner).Get<PbrLightmapSetResource>().Data;
        return new(set, prepared.Source.ReadAsync, (reference, record) => {
            var use = RenderAssetUse.Create(world);
            try {
                var data = AcquireProduct(world, prepared.Source, reference, record, use).Get<PbrLightmapResource>().Data;
                return (data, use.Destroy);
            }
            catch { use.Destroy(); throw; }
        }, prepared.Source.ReadAsync, (member, page, record) => {
            var use = RenderAssetUse.Create(world);
            try { return AcquirePage(world, prepared.Source, member, page, record, use).Get<PbrLightmapPageResource>().Data; }
            finally { use.Destroy(); }
        });
    }

    private static void RequireOwner(World world, Entity owner)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!owner.IsValid || !ReferenceEquals(owner.Host.World, world) || Unsafe.IsNullRef(ref owner.GetOrNullRef<AssetMetadata>()))
            throw new ArgumentException("Lighting owner must be an asset entity in the target World.", nameof(owner));
    }
}
