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
        private readonly Dictionary<AssetId, (MeshRasterPagesData Index, int Page)> _rasterPages = [];
        private readonly Dictionary<AssetId, PbrTextureMipReference> _mips = [];
        private readonly AssetChunkCache _cache;
        internal AssetChunkCacheStatistics Statistics => _cache.Statistics;

        internal PageSource(string scenePath, HttpClient client, Prepared<MeshVirtualRecord> prepared,
            long cacheBytes = 32L * 1024 * 1024)
            : this(scenePath, client, prepared, prepared.Products, new Dictionary<AssetId, MeshRasterPagesRecord>(), cacheBytes) { }

        internal PageSource(string scenePath, HttpClient client, Prepared<MeshRasterPagesRecord> prepared,
            long cacheBytes = 32L * 1024 * 1024)
            : this(scenePath, client, prepared, new Dictionary<AssetId, MeshVirtualRecord>(), prepared.Products, cacheBytes) { }

        private PageSource(string scenePath, HttpClient client, Prepared prepared,
            IReadOnlyDictionary<AssetId, MeshVirtualRecord> virtualProducts,
            IReadOnlyDictionary<AssetId, MeshRasterPagesRecord> rasterProducts, long cacheBytes)
        {
            var http = AssetInput.TryGetHttpUri(scenePath, out var uri);
            var root = http ? null : Path.GetDirectoryName(Path.GetFullPath(scenePath));
            var claims = new HashSet<AssetId>(prepared.Meshes.Keys.Concat(prepared.Materials.Keys).Concat(prepared.Textures.Keys).Append(prepared.Id));
            foreach (var mesh in prepared.Meshes.Values) {
                claims.Add(mesh.Data.Source);
                if (mesh.Data.Raster is { } raster) claims.Add(raster.Id);
                if (mesh.Data.RasterPages is { } rasterPages) claims.Add(rasterPages.Id);
                if (mesh.Data.Virtual is { } virtuals) claims.Add(virtuals.Id);
                if (mesh.Data.Bvh is { } bvh) claims.Add(bvh.Id);
            }
            var locations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var product in virtualProducts) {
                var index = product.Value.Data;
                if (index.ProductId != product.Key) throw new InvalidAssetException("Virtual index belongs to another logical product.");
                for (var p = 0; p < index.Pages.Length; p++) {
                    var page = index.Pages[p];
                    if (claims.Contains(page.Id)) throw new InvalidAssetException("Virtual page aliases another asset kind.");
                    if (!_pages.TryAdd(page.Id, (index, p))) {
                        var prior = _pages[page.Id];
                        if (prior.Index.Pages[prior.Page] != page) throw new InvalidAssetException("Page ID has conflicting revisions.");
                    }
                    locations.TryAdd(page.ContentHash, page.Id.Value.ToString("N") + ".siaasset");
                }
            }
            foreach (var product in rasterProducts) {
                var index = product.Value.Data;
                if (index.ProductId != product.Key) throw new InvalidAssetException("Raster page index belongs to another logical product.");
                for (var pageIndex = 0; pageIndex < index.Pages.Length; pageIndex++) {
                    var page = index.Pages[pageIndex];
                    if (claims.Contains(page.Id) || _pages.ContainsKey(page.Id))
                        throw new InvalidAssetException("Raster page aliases another asset kind.");
                    if (!_rasterPages.TryAdd(page.Id, (index, pageIndex))) {
                        var previous = _rasterPages[page.Id];
                        var prior = previous.Index.Pages[previous.Page];
                        if (prior.ContentHash != page.ContentHash || prior.EncodedBytes != page.EncodedBytes)
                            throw new InvalidAssetException("Raster page ID has conflicting revisions.");
                    }
                    locations.TryAdd(page.ContentHash, page.Id.Value.ToString("N") + ".siaasset");
                }
            }
            foreach (var texture in prepared.Textures.Values) {
                if (texture.Mips is not { } index) continue;
                foreach (var mip in index.Mips) {
                    if (claims.Contains(mip.Id) || _pages.ContainsKey(mip.Id) || _rasterPages.ContainsKey(mip.Id))
                        throw new InvalidAssetException("Texture mip aliases another scene asset.");
                    if (!_mips.TryAdd(mip.Id, mip) && !SameMip(_mips[mip.Id], mip))
                        throw new InvalidAssetException("Texture mip ID has conflicting revisions.");
                }
            }
            _cache = new(async (chunk, token) => {
                // Geometry products use an ID-to-file mapping. Authenticated typed
                // mip references already carry content IDs; resolve their location on demand.
                var name = locations.TryGetValue(chunk.Id, out var page) ? page : chunk.FileName;
                var path = http ? new UriBuilder(new Uri(uri!, name)) { Query = uri!.Query.TrimStart('?') }.Uri.AbsoluteUri
                    : Path.Combine(root!, name);
                return await AssetInput.OpenAsync(path, client, token);
            }, cacheBytes);
        }

        private static bool SameMip(PbrTextureMipReference a, PbrTextureMipReference b)
            => a.Id == b.Id && a.ContentHash == b.ContentHash && a.EncodedBytes == b.EncodedBytes
                && a.Content.Chunks.Length == b.Content.Chunks.Length
                && a.Content.Chunks.Zip(b.Content.Chunks).All(pair => pair.First.Id == pair.Second.Id && pair.First.Length == pair.Second.Length
                    && pair.First.DecodedLength == pair.Second.DecodedLength && pair.First.Encoding == pair.Second.Encoding);

        internal async Task<PbrTextureRecord> ReadMipAsync(PbrTextureMipReference reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_mips.TryGetValue(reference.Id, out var selected) || !SameMip(reference, selected))
                throw new AssetNotFoundException("Unknown typed texture mip revision.");
            return await ChunkLoader.LoadStreamingAsync<PbrTextureRecord, ulong>(selected.Content, _cache,
                (ulong)selected.EncodedBytes, maximumBytes: selected.EncodedBytes, priority: 100, cancellationToken: token).ConfigureAwait(false);
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

        internal async Task<MeshRasterPageRecord> ReadRasterAsync(AssetRefer<MeshRasterPageRecord>.Id reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_rasterPages.TryGetValue(reference.Value, out var use)) throw new AssetNotFoundException("Unknown typed Raster page.");
            var descriptor = use.Index.Pages[use.Page];
            // The synchronous codec's contiguous input is independently capped at one 256 KiB leaf.
            var record = await ChunkLoader.LoadAsync<MeshRasterPageRecord, ulong>(descriptor.Content, _cache,
                MeshRasterPageData.DefaultMaximumBytes, maximumBytes: descriptor.EncodedBytes,
                cancellationToken: token).ConfigureAwait(false);
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
            r => prepared.Products[r.Value].Data, r => prepared.Materials[r.Value].Data, r => prepared.Textures[r.Value].Data);
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

    internal static (Entity Root, PbrSceneStream Stream) AcquireStreaming(World world, PreparedStreaming prepared)
    {
        AssetLibrary.RegisterAsset<MeshVirtualPageAsset, MeshVirtualPageRecord>();
        var acquired = AcquireVirtual(world, prepared.Index);
        try {
            foreach (var page in prepared.Pages) world.AcquireAsset(page.Key, () => page.Value, acquired.Root);
            var stream = PbrSceneStream.Create(acquired.Scene,
                r => r.Find(world)?.Get<MeshVirtualPageAsset>().Data ?? throw new AssetNotFoundException(), prepared.Source.ReadAsync,
                (reference, record) => AcquirePage(world, reference, record), maximumRequiredBytes: prepared.MaximumRequiredBytes,
                textureMips: TextureMips(world, prepared), prepareMip: prepared.Source.ReadMipAsync);
            return (acquired.Root, stream);
        }
        catch { acquired.Root.Destroy(); throw; }
    }

    private static Func<PbrTextureData, PbrTextureMipsData?> TextureMips(World world, Prepared prepared)
    {
        var indices = new Dictionary<PbrTextureData, PbrTextureMipsData>(ReferenceEqualityComparer.Instance);
        foreach (var id in prepared.Textures.Keys) {
            var asset = new AssetRefer<PbrTextureRecord>.Id(id).Find(world)?.Get<PbrTextureAsset>() ?? throw new AssetNotFoundException();
            if (asset.Mips is { } index && !indices.TryAdd(asset.Data, index) && !ReferenceEquals(indices[asset.Data], index))
                throw new InvalidAssetException("One bootstrap texture has conflicting mip indices.");
        }
        return data => indices.GetValueOrDefault(data);
    }

    // Only the graphics owner's context invokes this short original Asset use.
    private static MeshVirtualPageData AcquirePage(World world, PageReference reference, MeshVirtualPageRecord record)
    {
        var use = RenderAssetUse.Create(world);
        try { return world.AcquireAsset(reference.Value, () => record, use).Get<MeshVirtualPageAsset>().Data; }
        finally { use.Destroy(); }
    }
}
