using System.Text.Json;
using System.Text.Json.Serialization;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Owns checksum-verified reads and startup data for one streaming renderer.</summary>
public sealed partial class PbrSceneStream : IAsyncDisposable
{
    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(Header))]
    private partial class StreamJsonContext : JsonSerializerContext;
    internal sealed record Detail(string Id, GeometryPage.Counts Size);
    private sealed record Geometry(int RootLength, Detail? Detail);
    private sealed record Instance(int Geometry, int Material, float[] Transform);
    private sealed record Header(int Version, byte[] Manifest, string[] Materials, int MaterialBytes, string[] Roots, Geometry[] Geometry, Instance[] Instances);
    private readonly AssetChunkCache _cache;
    private readonly AssetChunkManifest _manifest;
    internal Detail?[] Details { get; }
    internal GeometryPage[] RootPages { get; private set; }
    public ReadOnlyMemory<VisibilityInstance> Instances { get; }
    /// <summary>Resident materials and transparent geometry; opaque geometry uses GPU-ready pages.</summary>
    public PbrSceneAsset Bootstrap { get; }
    public Aabb Bounds { get; }
    public AssetChunkCacheStatistics Statistics => _cache.Statistics;

    private PbrSceneStream(AssetChunkCache cache, AssetChunkManifest manifest, Detail?[] details,
        PbrSceneAsset bootstrap, GeometryPage[] roots, VisibilityInstance[] instances)
    {
        _cache = cache; _manifest = manifest; Details = details; Bootstrap = bootstrap; RootPages = roots; Instances = instances;
        var minimum = new float3(float.PositiveInfinity); var maximum = new float3(float.NegativeInfinity);
        var bounds = roots.Select(page => page.Bounds).ToArray();
        foreach (var instance in instances) {
            var b = bounds[instance.AssetIndex];
            for (var i = 0; i < 8; i++) {
                var p = math.mul(instance.Transform, new float4((i & 1) == 0 ? b.Min.x : b.Max.x,
                    (i & 2) == 0 ? b.Min.y : b.Max.y, (i & 4) == 0 ? b.Min.z : b.Max.z, 1)).xyz;
                minimum = math.min(minimum, p); maximum = math.max(maximum, p);
            }
        }
        Bounds = new(minimum, maximum);
    }

    public static async Task<PbrSceneStream> OpenAsync(ReadOnlyMemory<byte> metadata,
        Func<AssetChunk, CancellationToken, ValueTask<Stream>> open, long byteBudget = 32 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (metadata.Length > AssetChunkManifest.MaximumBytes) throw new InvalidDataException("Scene metadata exceeds its limit.");
        var header = JsonSerializer.Deserialize(metadata.Span, StreamJsonContext.Default.Header) ?? throw new InvalidDataException("Missing scene metadata.");
        if (header.Version != 1 || header.Manifest is null || header.Geometry is null || header.Geometry.Length is 0 or > 4096
            || header.Instances is null || header.Instances.Length is 0 or > 1000000 || header.Materials is null || header.Roots is null
            || header.MaterialBytes is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("Invalid scene stream metadata.");
        var manifest = AssetChunkManifest.Decode(header.Manifest);
        long rootLength = 0;
        foreach (var geometry in header.Geometry) {
            if (geometry is null || geometry.RootLength is < 24 or > AssetChunk.MaximumLength) throw new InvalidDataException("Invalid root length.");
            rootLength += geometry.RootLength;
            if (geometry.Detail is { } detail) {
                var size = detail.Size;
                if (size.Vertices <= 0 || size.Indices <= 0 || size.Triangles <= 0 || size.Clusters <= 0
                    || size.ByteLength > AssetChunk.MaximumLength || manifest.GetChunk(detail.Id).Length < 12) throw new InvalidDataException("Invalid detail reservation.");
            }
        }
        if (rootLength > 512 * 1024 * 1024)
            throw new InvalidDataException("Root pages exceed their startup budget or have inconsistent lengths.");
        var cache = new AssetChunkCache(open, byteBudget);
        try {
            var materialBytes = await ReadPartsAsync(header.Materials, header.MaterialBytes);
            var bootstrap = await Task.Run(() => PbrSceneAsset.Decode(materialBytes, 256 * 1024 * 1024, cancellationToken), cancellationToken);
            var rootBytes = await ReadPartsAsync(header.Roots, (int)rootLength);
            var roots = await Task.Run(() => {
                var result = new GeometryPage[header.Geometry.Length]; var offset = 0;
                for (var i = 0; i < result.Length; i++) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = header.Geometry[i].RootLength;
                    result[i] = GeometryPage.Decode(rootBytes.AsMemory(offset, length)); offset += length;
                }
                return result;
            }, cancellationToken);
            var instances = new VisibilityInstance[header.Instances.Length];
            for (var i = 0; i < instances.Length; i++) {
                var instance = header.Instances[i];
                if (instance is null || (uint)instance.Geometry >= roots.Length || (uint)instance.Material >= bootstrap.Materials.Length
                    || bootstrap.Materials.Span[instance.Material].AlphaBlend || instance.Transform is not { Length: 16 } values || values.Any(v => !float.IsFinite(v)))
                    throw new InvalidDataException("Invalid streamed instance.");
                var transform = new float4x4(new(values[0], values[1], values[2], values[3]), new(values[4], values[5], values[6], values[7]),
                    new(values[8], values[9], values[10], values[11]), new(values[12], values[13], values[14], values[15]));
                var determinant = math.determinant(transform);
                if (!float.IsFinite(determinant) || determinant <= 1e-12f || values[3] != 0 || values[7] != 0 || values[11] != 0 || values[15] != 1)
                    throw new InvalidDataException("Invalid instance transform.");
                instances[i] = new(transform, instance.Material) { AssetIndex = instance.Geometry };
            }
            return new(cache, manifest, header.Geometry.Select(g => g.Detail).ToArray(), bootstrap, roots, instances);

            async Task<byte[]> ReadPartsAsync(string[] ids, int length) {
                if (ids.Length > AssetChunkManifest.MaximumChunks) throw new InvalidDataException("Too many startup parts.");
                var bytes = new byte[length]; var offset = 0;
                foreach (var id in ids) {
                    using var lease = await cache.AcquireAsync(manifest.GetChunk(id), cancellationToken: cancellationToken);
                    var decoded = await Task.Run(() => SceneStreamBlock.Decode(lease.Memory.Span, System.Math.Min(4 * 1024 * 1024, length - offset)), cancellationToken);
                    decoded.CopyTo(bytes, offset); offset += decoded.Length;
                }
                if (offset != length) throw new InvalidDataException("Startup decoded lengths do not match metadata.");
                return bytes;
            }
        } catch { await cache.DisposeAsync(); throw; }
    }

    internal void ReleaseRootPages() => RootPages = [];
    internal ValueTask<AssetChunkLease> AcquireAsync(int geometry, CancellationToken cancellationToken)
        => _cache.AcquireAsync(_manifest.GetChunk(Details[geometry]!.Id), cancellationToken: cancellationToken);
    public ValueTask DisposeAsync() => _cache.DisposeAsync();

    /// <summary>Cooks immutable chunks. Publish the returned metadata only after every chunk is durable.</summary>
    public static async Task<byte[]> CookAsync(PbrSceneAsset source,
        Func<AssetChunk, ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(write);
        // A geometry with no patch-tree nodes carries no triangles (for example a collapsed
        // duplicate left behind by an asset-level dedup pass). GeometryPage's cooked root format
        // has no representation for an empty page, and drawing nothing is already the correct
        // rendered result, so opaque instances pointing at it are dropped before cooking instead
        // of reaching GeometryPage.Cook, which would otherwise throw on the resulting zero counts.
        var opaque = source.Instances.ToArray()
            .Where(i => !source.Materials.Span[i.Material].AlphaBlend && source.Geometry.Span[i.Geometry].Build.Tree.Nodes.Length > 0)
            .ToArray();
        var transparent = source.Instances.ToArray().Where(i => source.Materials.Span[i.Material].AlphaBlend).ToArray();
        var opaqueGeometry = opaque.Select(i => i.Geometry).Distinct().Order().ToArray();
        if (opaqueGeometry.Length is 0 or > 4096 || opaque.Length > 1000000)
            throw new ArgumentException("Streaming requires 1–4096 opaque geometries and at most one million opaque instances.", nameof(source));
        var transparentGeometry = transparent.Select(i => i.Geometry).Distinct().ToArray();
        var bootstrap = PbrSceneAsset.Create(transparentGeometry.Select(i => source.Geometry.Span[i].ExtractFinest()).ToArray(),
            source.Materials.Span, transparent.Select(i => i with { Geometry = Array.IndexOf(transparentGeometry, i.Geometry) }).ToArray(), source.Attribution).Encode(cancellationToken);
        var chunks = new Dictionary<string, AssetChunk>(StringComparer.Ordinal);
        if (bootstrap.Length > 64 * 1024 * 1024) throw new ArgumentException("Resident materials and transparent geometry exceed their startup budget.", nameof(source));
        var materialParts = await WritePartsAsync(bootstrap);
        var geometry = new Geometry[opaqueGeometry.Length];
        using var rootData = new MemoryStream();
        for (var i = 0; i < geometry.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = source.Geometry.Span[opaqueGeometry[i]];
            var root = GeometryPage.Cook(asset.ExtractRoots());
            rootData.Write(root.Bytes.Span);
            Detail? detail = null;
            if (asset.Build.Tree.Nodes.Length > asset.Build.Tree.RootCount) {
                var page = GeometryPage.Cook(asset); var encoded = SceneStreamBlock.Encode(page.Bytes.Span); var chunk = AssetChunk.FromBytes(encoded);
                if (chunks.TryAdd(chunk.Id, chunk)) await write(chunk, encoded, cancellationToken);
                detail = new(chunk.Id, page.Size);
            }
            geometry[i] = new(root.Bytes.Length, detail);
        }
        if (rootData.Length > 512 * 1024 * 1024) throw new ArgumentException("Root geometry exceeds its startup budget.");
        var rootParts = await WritePartsAsync(rootData.GetBuffer().AsMemory(0, (int)rootData.Length));
        var manifest = new AssetChunkManifest(chunks.Values, materialParts.Concat(rootParts).Distinct());
        var instances = opaque.Select(i => {
            var t = i.Transform;
            return new Instance(Array.IndexOf(opaqueGeometry, i.Geometry), i.Material,
                [t.c0.x,t.c0.y,t.c0.z,t.c0.w,t.c1.x,t.c1.y,t.c1.z,t.c1.w,t.c2.x,t.c2.y,t.c2.z,t.c2.w,t.c3.x,t.c3.y,t.c3.z,t.c3.w]);
        }).ToArray();
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new Header(1, manifest.Encode(), materialParts, bootstrap.Length, rootParts, geometry, instances), StreamJsonContext.Default.Header);
        if (metadata.Length > AssetChunkManifest.MaximumBytes) throw new ArgumentException("Scene metadata exceeds its limit.");
        return metadata;

        async Task<string[]> WritePartsAsync(ReadOnlyMemory<byte> bytes) {
            var ids = new List<string>();
            for (var offset = 0; offset < bytes.Length; offset += 4 * 1024 * 1024) {
                var part = bytes.Slice(offset, System.Math.Min(4 * 1024 * 1024, bytes.Length - offset));
                var encoded = SceneStreamBlock.Encode(part.Span); var chunk = AssetChunk.FromBytes(encoded);
                if (chunks.TryAdd(chunk.Id, chunk)) await write(chunk, encoded, cancellationToken);
                ids.Add(chunk.Id);
            }
            return ids.ToArray();
        }
    }
}
