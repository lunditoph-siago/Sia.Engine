using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;

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
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }

    internal static async Task<PreparedMixed> PrepareMixedAsync(string path, HttpClient client, RenderQuality quality,
        bool tracing = false, Func<PbrMeshInstance, MeshManifest, PbrMeshPresentation>? select = null,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default, long maximumRequiredBytes = 512L * 1024 * 1024,
        IReadOnlySet<Guid>? rasterInstances = null)
    {
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRequiredBytes);
        var reader = new Reader(path, client, maximumFileBytes, maximumTotalBytes, cancellationToken);
        var prepared = await reader.References(); var choices = new Dictionary<Guid, PbrMeshPresentation>();
        var rasters = new Dictionary<AssetId, MeshRasterRecord>(); var virtuals = new Dictionary<AssetId, MeshVirtualRecord>();
        var bvhs = new Dictionary<AssetId, MeshBvhRecord>();
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
        }
        if (rasterInstances is not null)
            foreach (var id in rasterInstances)
                if (!choices.ContainsKey(id)) throw new ArgumentException("Requested conventional instance is absent from the scene.", nameof(rasterInstances));
        for (var i = 0; i < prepared.Scene.Data.Instances.Length; i++) {
            var instance = prepared.Scene.Data.Instances[i];
            cancellationToken.ThrowIfCancellationRequested();
            var mesh = prepared.Meshes[prepared.Scene.Data.Bindings[instance.Binding].Mesh.Value].Data;
            if (choices[instance.Id] == PbrMeshPresentation.Raster) {
                var product = mesh.Raster ?? throw new InvalidAssetException("Selected Mesh has no Raster product.");
                if (!rasters.ContainsKey(product.Id)) rasters.Add(product.Id, await reader.Read<MeshRasterRecord>(product.Id, MeshRasterRecord.Load));
            }
            else {
                var product = mesh.Virtual ?? throw new InvalidAssetException("Selected Mesh has no Virtual product.");
                if (!virtuals.ContainsKey(product.Id)) virtuals.Add(product.Id, await reader.Read<MeshVirtualRecord>(product.Id, MeshVirtualRecord.Load));
            }
            if (tracing) {
                var product = mesh.Bvh ?? throw new InvalidAssetException("Selected trace Mesh has no independent BVH product.");
                if (!bvhs.ContainsKey(product.Id)) bvhs.Add(product.Id, await reader.Read<MeshBvhRecord>(product.Id, MeshBvhRecord.Load));
            }
        }
        var mixed = PbrSceneComposition.ResolveMixed(prepared.Scene.Data, i => choices[i.Id], r => prepared.Meshes[r.Value].Data,
            r => rasters[r.Value].Data, r => virtuals[r.Value].Data, r => prepared.Materials[r.Value].Data, r => prepared.Textures[r.Value].Data,
            cancellationToken: cancellationToken);
        if (tracing) _ = PbrSceneComposition.ResolveBvh(prepared.Scene.Data, r => prepared.Meshes[r.Value].Data,
            r => bvhs[r.Value].Data, r => prepared.Materials[r.Value].Data, r => prepared.Textures[r.Value].Data, cancellationToken);
        var required = PbrSceneStream.RequiredPages(mixed.Virtual, maximumRequiredBytes); var total = reader.EncodedBytes;
        foreach (var page in required) {
            if (page.EncodedBytes > maximumFileBytes || page.EncodedBytes > maximumTotalBytes - total)
                throw new InvalidDataException("Required typed pages exceed the scene encoded admission.");
            total += page.EncodedBytes;
        }
        var virtualInputs = new Prepared<MeshVirtualRecord>(prepared.Id, prepared.Scene, prepared.Meshes, virtuals, prepared.Materials, prepared.Textures);
        var source = new PageSource(path, client, virtualInputs);
        try {
            var pages = new Dictionary<AssetId, MeshVirtualPageRecord>();
            foreach (var page in required) pages.Add(page.Id, await source.ReadAsync(page.Reference, cancellationToken));
            return new(prepared.Id, prepared.Scene, prepared.Meshes, prepared.Materials, prepared.Textures,
                rasters, virtuals, bvhs, choices, pages, source, maximumRequiredBytes) { EncodedBytes = total };
        }
        catch { await source.DisposeAsync(); throw; }
    }

    internal static (Entity Root, PbrSceneStream Stream, PbrBvhScene? Trace) AcquireMixed(World world, PreparedMixed prepared)
    {
        AssetLibrary.RegisterAsset<MeshRasterAsset, MeshRasterRecord>(); AssetLibrary.RegisterAsset<MeshVirtualAsset, MeshVirtualRecord>();
        AssetLibrary.RegisterAsset<MeshBvhAsset, MeshBvhRecord>(); AssetLibrary.RegisterAsset<MeshVirtualPageAsset, MeshVirtualPageRecord>();
        var acquired = Acquire(world, prepared, (w, root) => {
            foreach (var item in prepared.Rasters) w.AcquireAsset(item.Key, () => item.Value, root);
            foreach (var item in prepared.Virtuals) w.AcquireAsset(item.Key, () => item.Value, root);
            foreach (var item in prepared.Bvhs) w.AcquireAsset(item.Key, () => item.Value, root);
            foreach (var item in prepared.Pages) w.AcquireAsset(item.Key, () => item.Value, root);
        }, (w, scene) => {
            MeshManifest Mesh(AssetRefer<MeshManifestRecord>.Id r) => r.Find(w)?.Get<MeshManifestAsset>().Data ?? throw new AssetNotFoundException();
            PbrMaterialData Material(AssetRefer<PbrMaterialRecord>.Id r) => r.Find(w)?.Get<PbrMaterialResource>().Data ?? throw new AssetNotFoundException();
            PbrTextureData Texture(AssetRefer<PbrTextureRecord>.Id r) => r.Find(w)?.Get<PbrTextureAsset>().Data ?? throw new AssetNotFoundException();
            var mixed = PbrSceneComposition.ResolveMixed(scene, i => prepared.Choices[i.Id], Mesh,
                r => r.Find(w)?.Get<MeshRasterAsset>().Data ?? throw new AssetNotFoundException(),
                r => r.Find(w)?.Get<MeshVirtualAsset>().Data ?? throw new AssetNotFoundException(), Material, Texture);
            var stream = PbrSceneStream.Create(mixed, r => r.Find(w)?.Get<MeshVirtualPageAsset>().Data ?? throw new AssetNotFoundException(),
                prepared.Source.ReadAsync, (reference, record) => AcquirePage(w, reference, record), prepared.MaximumRequiredBytes);
            var trace = prepared.Bvhs.Count == 0 ? null : PbrSceneComposition.ResolveBvh(scene, Mesh,
                r => r.Find(w)?.Get<MeshBvhAsset>().Data ?? throw new AssetNotFoundException(), Material, Texture);
            return (Stream: stream, Trace: trace);
        });
        return (acquired.Root, acquired.Scene.Stream, acquired.Scene.Trace);
    }
}
