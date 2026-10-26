using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    internal sealed record PreparedRasterPagesStreaming(Prepared<MeshRasterPagesRecord> Index, PageSource Source)
        : Prepared(Index.Id, Index.Scene, Index.Meshes, Index.Materials, Index.Textures), IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }

    /// <summary>Prepares the complete metadata graph; no Source, whole Raster or geometry leaf is read.</summary>
    internal static async Task<PreparedRasterPagesStreaming> PrepareRasterPagesStreamingAsync(string path, HttpClient client,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(path, client,
            static manifest => manifest.RasterPages?.Id ?? throw new InvalidDataException("Selected Mesh has no paged Raster product."),
            static (MeshRasterPagesRecord record) => record.Data.Sections.Length, MeshRasterPagesRecord.Load,
            maximumFileBytes, maximumTotalBytes, cancellationToken);
        _ = PbrSceneComposition.ResolveRasterPages(prepared.Scene.Data, r => prepared.Meshes[r.Value].Data,
            r => prepared.Products[r.Value].Data, r => prepared.Materials[r.Value].Data, r => prepared.Textures[r.Value].Data,
            cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var source = new PageSource(path, client, prepared);
        return new(prepared, source) { EncodedBytes = prepared.EncodedBytes };
    }

    internal static (Entity Root, PbrRasterPagesScene Scene) AcquireRasterPages(World world, PreparedRasterPagesStreaming prepared)
    {
        AssetLibrary.RegisterAsset<MeshRasterPagesAsset, MeshRasterPagesRecord>();
        return Acquire(world, prepared.Index, static (owner, scene) => PbrSceneComposition.ResolveRasterPages(scene,
            r => r.Find(owner)?.Get<MeshManifestAsset>().Data ?? throw new AssetNotFoundException(),
            r => r.Find(owner)?.Get<MeshRasterPagesAsset>().Data ?? throw new AssetNotFoundException(),
            r => r.Find(owner)?.Get<PbrMaterialResource>().Data ?? throw new AssetNotFoundException(),
            r => r.Find(owner)?.Get<PbrTextureAsset>().Data ?? throw new AssetNotFoundException()));
    }

    internal static MeshRasterPageData AcquireRasterPage(World world, AssetRefer<MeshRasterPageRecord>.Id reference, MeshRasterPageRecord prepared)
    {
        AssetLibrary.RegisterAsset<MeshRasterPageAsset, MeshRasterPageRecord>();
        var root = RenderAssetUse.Create(world);
        try {
            var data = world.AcquireAsset(reference.Value, () => prepared, root).Get<MeshRasterPageAsset>().Data;
            if (data.ContentHash != prepared.Data.ContentHash) throw new InvalidAssetException("Acquired Raster page has another content revision.");
            return data;
        }
        finally { root.Destroy(); }
    }
}
