using Sia;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using ProductReference = Sia.Asset.AssetRefer<Sia.Engine.Rendering.Pbr.PbrLightmapRecord>.Id;

namespace Sia.Engine.Example;

internal static partial class LightmapFiles
{
    // Only transport and encoded-cache ownership live here. Decoded members are
    // returned to the caller; none is retained in a second asset/lifetime cache.
    internal sealed class ProductSource : IAsyncDisposable
    {
        private sealed class Route(string path, int length)
        {
            internal readonly string Path = path;
            internal readonly int Length = length;
            internal int Users = 1;
        }

        private readonly Lock _gate = new();
        private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
        private readonly Dictionary<AssetId, int> _products = [];
        private readonly HashSet<AssetId> _otherIds;
        private readonly string _directory;
        private readonly Uri? _uri;
        private readonly HttpClient _http;
        private readonly AssetChunkCache _cache;
        private readonly ulong _maximumProductBytes;
        private readonly long _cacheBytes;
        private Task? _dispose;
        private const int MaximumRoutes = 128;
        internal PbrLightmapSetData Set { get; }
        internal AssetChunkCacheStatistics Statistics => _cache.Statistics;

        internal ProductSource(string setPath, HttpClient http, PbrLightmapSetData set,
            long cacheBytes, ulong maximumProductBytes = PbrLightmapData.MaximumEncodedBytes,
            IEnumerable<AssetId>? otherIds = null)
        {
            ArgumentNullException.ThrowIfNull(setPath); ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(set); ArgumentOutOfRangeException.ThrowIfLessThan(cacheBytes, 2);
            if (maximumProductBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumProductBytes));
            Set = set; _http = http; _maximumProductBytes = maximumProductBytes; _cacheBytes = cacheBytes;
            _otherIds = otherIds is null ? [] : new(otherIds);
            if (_otherIds.Contains(set.ProductId)) throw new InvalidAssetException("Lighting set aliases another selected asset.");
            for (var at = 0; at < set.Products.Length; at++) {
                var product = set.Products[at];
                if (_otherIds.Contains(product.Id)) throw new InvalidAssetException("Lighting member aliases another selected asset.");
                if ((long)product.EncodedBytes + 1 > cacheBytes)
                    throw new ArgumentException("Lighting member cannot fit the encoded cache admission.", nameof(cacheBytes));
                _products.Add(product.Id, at);
            }
            var remote = AssetInput.TryGetHttpUri(setPath, out var uri);
            _uri = remote ? uri : null;
            _directory = remote ? "" : Path.GetDirectoryName(Path.GetFullPath(setPath))!;
            _cache = new(OpenAsync, cacheBytes, maximumPendingChunks: MaximumRoutes);
        }

        internal int FindProduct(ProductReference reference)
        {
            ArgumentNullException.ThrowIfNull(reference);
            return _products.TryGetValue(reference.Value, out var at) ? at
                : throw new AssetNotFoundException("Lighting member is not declared by this set.");
        }

        internal async Task<PbrLightmapRecord> ReadAsync(ProductReference reference, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var at = FindProduct(reference); var expected = Set.Products[at];
            var counts = Set.ProductCounts(at);
            if (PbrLightmapData.RequiredBytes(counts.Receivers, counts.Charts, counts.Pages) > _maximumProductBytes)
                throw new InvalidDataException("Lighting member exceeds the decoded metadata admission.");
            Register(expected.ContentHash, expected.Id, expected.EncodedBytes);
            try {
                var record = await ChunkLoader.LoadAsync<PbrLightmapRecord, ulong>(expected.Content, _cache,
                    _maximumProductBytes, maximumBytes: expected.EncodedBytes, cancellationToken: token).ConfigureAwait(false);
                ValidateProduct(at, record.Data, token); return record;
            }
            finally { Release(expected.ContentHash); }
        }

        // The member must have been prepared through the verified content path
        // before requesting its pages. Acquisition and GPU use remain caller-owned.
        internal async Task<PbrLightmapPageRecord> ReadAsync(PbrLightmapData member, int page, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(member); token.ThrowIfCancellationRequested();
            ValidatePage(member, page, token);
            var descriptor = member.Pages[page];
            Register(descriptor.ContentHash, descriptor.Id, descriptor.EncodedBytes);
            try {
                var record = await ChunkLoader.LoadAsync(descriptor.Content, _cache,
                    maximumBytes: descriptor.EncodedBytes, cancellationToken: token).ConfigureAwait(false);
                member.ValidatePage(page, record.Data); token.ThrowIfCancellationRequested(); return record;
            }
            finally { Release(descriptor.ContentHash); }
        }

        internal void ValidateProduct(int at, PbrLightmapData member, CancellationToken token = default)
        {
            Set.ValidateProduct(at, member, token);
            foreach (var page in member.Pages) {
                token.ThrowIfCancellationRequested();
                ValidatePageIdentity(page.Id);
            }
        }

        internal void ValidatePage(PbrLightmapData member, int page, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(member);
            Set.ValidateProduct(FindProduct(new(member.ProductId)), member, token);
            if ((uint)page >= member.Pages.Length) throw new ArgumentOutOfRangeException(nameof(page));
            ValidatePageIdentity(member.Pages[page].Id);
        }

        private void ValidatePageIdentity(AssetId id)
        {
            if (id == Set.ProductId || _products.ContainsKey(id) || _otherIds.Contains(id))
                throw new InvalidAssetException("Lighting page aliases a selected set/member/scene identity.");
        }

        private string Location(AssetId id)
        {
            var filename = id.Value.ToString("N") + ".siaasset";
            return _uri is null ? Path.Combine(_directory, filename) : new Uri(_uri, filename).AbsoluteUri;
        }

        // Routes exist only while original ChunkLoader calls are alive. A shared
        // content hash has one route even when independently named slots coalesce.
        private void Register(string hash, AssetId id, int length)
        {
            lock (_gate) {
                ObjectDisposedException.ThrowIf(_dispose is not null, this);
                if ((long)length + 1 > _cacheBytes) throw new ArgumentException("Lighting content exceeds the encoded cache admission.");
                if (_routes.TryGetValue(hash, out var route)) {
                    if (route.Length != length) throw new InvalidDataException("Lighting content has conflicting declared lengths.");
                    route.Users = checked(route.Users + 1);
                }
                else {
                    if (_routes.Count == MaximumRoutes) throw new InvalidOperationException("Lighting transport route admission is full.");
                    _routes.Add(hash, new(Location(id), length));
                }
            }
        }

        private void Release(string hash)
        {
            lock (_gate) { if (--_routes[hash].Users == 0) _routes.Remove(hash); }
        }

        private async ValueTask<Stream> OpenAsync(AssetChunk chunk, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path;
            lock (_gate) {
                if (!_routes.TryGetValue(chunk.Id, out var route) || route.Length != chunk.Length)
                    throw new AssetNotFoundException("Lighting content has no active transport route.");
                path = route.Path;
            }
            return await AssetInput.OpenAsync(path, _http, token).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate) { return new(_dispose ??= _cache.DisposeAsync().AsTask()); }
        }
    }

    internal static Entity AcquireSet(World world, ProductSource source, PbrLightmapSetRecord record,
        PbrSceneData scene, Entity owner)
    {
        RequireOwner(world, owner); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(record);
        if (!source.Set.SameRevision(record.Data)) throw new InvalidAssetException("Prepared lighting set differs from its transport source.");
        record.Data.ValidateScene(scene,
            r => r.Find(world)?.Get<MeshManifestAsset>().Data ?? throw new AssetNotFoundException(),
            r => r.Find(world)?.Get<PbrMaterialResource>().Data ?? throw new AssetNotFoundException(),
            r => r.Find(world)?.Get<PbrTextureAsset>().Data ?? throw new AssetNotFoundException());
        AssetLibrary.RegisterAsset<PbrLightmapSetResource, PbrLightmapSetRecord>();
        var reference = new AssetRefer<PbrLightmapSetRecord>.Id(record.Data.ProductId);
        if (reference.Find(world) is { } existing && !record.Data.SameRevision(existing.Get<PbrLightmapSetResource>().Data))
            throw new InvalidAssetException("A different lighting set revision is already acquired under this ID.");
        return world.AcquireAsset(reference.Value, () => record, owner);
    }

    internal static Entity AcquireProduct(World world, ProductSource source, ProductReference reference,
        PbrLightmapRecord record, Entity owner)
    {
        RequireOwner(world, owner); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(record);
        var at = source.FindProduct(reference); source.ValidateProduct(at, record.Data);
        AssetLibrary.RegisterAsset<PbrLightmapResource, PbrLightmapRecord>();
        if (reference.Find(world) is { } existing && !record.Data.SameRevision(existing.Get<PbrLightmapResource>().Data))
            throw new InvalidAssetException("A different lighting member revision is already acquired under this ID.");
        return world.AcquireAsset(reference.Value, () => record, owner);
    }

    internal static Entity AcquirePage(World world, ProductSource source, PbrLightmapData member,
        int page, PbrLightmapPageRecord record, Entity owner)
    {
        RequireOwner(world, owner); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(record);
        source.ValidatePage(member, page); member.ValidatePage(page, record.Data);
        var reference = member.Pages[page].Reference;
        AssetLibrary.RegisterAsset<PbrLightmapPageResource, PbrLightmapPageRecord>();
        if (reference.Find(world) is { } existing) member.ValidatePage(page, existing.Get<PbrLightmapPageResource>().Data);
        return world.AcquireAsset(reference.Value, () => record, owner);
    }
}
