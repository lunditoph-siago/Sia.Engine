using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using PageReference = Sia.Asset.AssetRefer<Sia.Engine.Mesh.MeshVirtualPageRecord>.Id;

namespace Sia.Engine.Example;

internal static partial class PbrSceneFiles
{
    internal sealed record PreparedStreaming(Prepared<MeshVirtualRecord> Index,
        IReadOnlyDictionary<AssetId, MeshVirtualPageRecord> Pages, PageSource Source, long MaximumRequiredBytes)
        : Prepared(Index.Id, Index.Scene, Index.Meshes, Index.Materials, Index.Textures), IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }

    // The application owns location policy and transport. The original cache/loader owns encoded admission.
    internal sealed class PageSource : IAsyncDisposable
    {
        private readonly Dictionary<AssetId, (MeshVirtualPagesData Index, int Page)> _pages = [];
        private readonly AssetChunkCache _cache;
        internal AssetChunkCacheStatistics Statistics => _cache.Statistics;

        internal PageSource(string scenePath, HttpClient client, Prepared<MeshVirtualRecord> prepared,
            long cacheBytes = 32L * 1024 * 1024)
        {
            var http = AssetInput.TryGetHttpUri(scenePath, out var uri);
            var root = http ? null : Path.GetDirectoryName(Path.GetFullPath(scenePath));
            var claims = new HashSet<AssetId>(prepared.Meshes.Keys.Concat(prepared.Materials.Keys).Concat(prepared.Textures.Keys).Append(prepared.Id));
            foreach (var mesh in prepared.Meshes.Values) {
                claims.Add(mesh.Data.Source);
                if (mesh.Data.Raster is { } raster) claims.Add(raster.Id);
                if (mesh.Data.Virtual is { } virtuals) claims.Add(virtuals.Id);
                if (mesh.Data.Bvh is { } bvh) claims.Add(bvh.Id);
            }
            var locations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var product in prepared.Products) {
                var index = product.Value.Data;
                if (index.ProductId != product.Key) throw new InvalidAssetException("Virtual index belongs to another logical product.");
                for (var p = 0; p < index.Pages.Length; p++) {
                    var page = index.Pages[p];
                    if (claims.Contains(page.Id)) throw new InvalidAssetException("Virtual page aliases another asset kind.");
                    if (!_pages.TryAdd(page.Id, (index, p))) {
                        var prior = _pages[page.Id];
                        if (prior.Index.Pages[prior.Page] != page) throw new InvalidAssetException("Page ID has conflicting revisions.");
                    }
                    locations.TryAdd(page.ContentHash, http ? new Uri(uri!, page.Id.Value.ToString("N") + ".siaasset").AbsoluteUri
                        : Path.Combine(root!, page.Id.Value.ToString("N") + ".siaasset"));
                }
            }
            _cache = new(async (chunk, token) => await AssetInput.OpenAsync(locations[chunk.Id], client, token), cacheBytes);
        }

        internal async Task<MeshVirtualPageRecord> ReadAsync(PageReference reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_pages.TryGetValue(reference.Value, out var use)) throw new AssetNotFoundException("Unknown typed virtual page.");
            var descriptor = use.Index.Pages[use.Page];
            var record = await ChunkLoader.LoadAsync(descriptor.Content, _cache,
                maximumBytes: MeshVirtualPageData.MaximumEncodedBytes, cancellationToken: token).ConfigureAwait(false);
            use.Index.ValidatePage(use.Page, record.Data, token);
            return record;
        }
        public ValueTask DisposeAsync() => _cache.DisposeAsync();
    }

    internal static async Task<PreparedStreaming> PrepareStreamingAsync(string path, HttpClient client,
        int maximumFileBytes = 128 * 1024 * 1024, long maximumTotalBytes = 512L * 1024 * 1024,
        CancellationToken cancellationToken = default, long maximumRequiredBytes = 512L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRequiredBytes);
        var prepared = await PrepareVirtualAsync(path, client, maximumFileBytes, maximumTotalBytes, cancellationToken);
        var scene = PbrSceneComposition.ResolveVirtual(prepared.Scene.Data, r => prepared.Meshes[r.Value].Data,
            r => prepared.Products[r.Value].Data, r => prepared.Materials[r.Value].Data, r => prepared.Textures[r.Value].Data, out _);
        var required = PbrSceneStream.RequiredPages(scene, maximumRequiredBytes);
        var total = prepared.EncodedBytes;
        foreach (var page in required) {
            if (page.EncodedBytes > maximumFileBytes || page.EncodedBytes > maximumTotalBytes - total)
                throw new InvalidDataException("Required typed pages exceed the scene encoded admission.");
            total += page.EncodedBytes;
        }
        var source = new PageSource(path, client, prepared);
        try {
            var pages = new Dictionary<AssetId, MeshVirtualPageRecord>();
            foreach (var page in required) pages.Add(page.Id, await source.ReadAsync(page.Reference, cancellationToken));
            return new(prepared, pages, source, maximumRequiredBytes) { EncodedBytes = total };
        }
        catch { await source.DisposeAsync(); throw; }
    }

    internal static (Entity Root, PbrSceneStream Stream, Guid[] SourceInstances) AcquireStreaming(World world, PreparedStreaming prepared)
    {
        AssetLibrary.RegisterAsset<MeshVirtualPageAsset, MeshVirtualPageRecord>();
        var acquired = AcquireVirtual(world, prepared.Index);
        try {
            foreach (var page in prepared.Pages) world.AcquireAsset(page.Key, () => page.Value, acquired.Root);
            var stream = PbrSceneStream.Create(acquired.Scene,
                r => r.Find(world)?.Get<MeshVirtualPageAsset>().Data ?? throw new AssetNotFoundException(), prepared.Source.ReadAsync,
                (reference, record) => {
                    // Only the graphics owner's context invokes this callback. This short use does not pin all detail pages.
                    var use = RenderAssetUse.Create(world);
                    try { return world.AcquireAsset(reference.Value, () => record, use).Get<MeshVirtualPageAsset>().Data; }
                    finally { use.Destroy(); }
                }, maximumRequiredBytes: prepared.MaximumRequiredBytes);
            return (acquired.Root, stream, acquired.SourceInstances);
        }
        catch { acquired.Root.Destroy(); throw; }
    }
}
