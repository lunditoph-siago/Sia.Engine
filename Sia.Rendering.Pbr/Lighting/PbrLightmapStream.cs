using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>An independently readable receiver-local coefficient page, with a one-texel filter border.</summary>
public readonly record struct PbrLightmapPage(int Receiver, int Mip, int X, int Y, AssetChunk Chunk);

/// <summary>Immutable compact lightmap metadata and an owned, bounded chunk-read cache.</summary>
public sealed partial class PbrLightmapStream : IAsyncDisposable
{
    public const int MaximumMetadataBytes = 32 * 1024 * 1024;
    public const int PageResolution = 32;
    public const int PageBorder = 1;
    public const int PageSide = PageResolution + PageBorder * 2;
    public const int PageBytes = PageSide * PageSide * 16;
    private const int HeaderBytes = 228;
    private const int BakeIdentityOffset = 92;
    private readonly AssetChunkCache _cache;
    private bool _disposed;

    public int Resolution { get; }
    public ReadOnlyMemory<PbrLightmapReceiver> Receivers { get; }
    public ReadOnlyMemory<PbrLightmapChart> Charts { get; }
    public ReadOnlyMemory<PbrLightmapPage> Pages { get; }
    public ReadOnlyMemory<byte> SceneIdentity { get; }
    public ReadOnlyMemory<byte> SurfaceIdentity { get; }
    public ReadOnlyMemory<byte> BakeIdentity { get; }
    public PbrLightmapBakeSettings Settings { get; }
    public AssetChunkCacheStatistics Statistics => _cache.Statistics;
    internal ReadOnlyMemory<float4> DecodeScales { get; }
    internal ReadOnlyMemory<byte> CoarseCoefficients { get; }

    private PbrLightmapStream(int resolution, PbrLightmapReceiver[] receivers, PbrLightmapChart[] charts,
        PbrLightmapPage[] pages, byte[] scene, byte[] surface, byte[] bake, PbrLightmapBakeSettings settings,
        float4[] scales, byte[] coarse, AssetChunkCache cache)
    {
        Resolution = resolution; Receivers = receivers; Charts = charts; Pages = pages;
        SceneIdentity = scene; SurfaceIdentity = surface; BakeIdentity = bake; Settings = settings;
        DecodeScales = scales; CoarseCoefficients = coarse; _cache = cache;
    }

    // Metadata already contains a valid coarse field. Opening never reads a fine chunk.
    public static Task<PbrLightmapStream> OpenAsync(ReadOnlyMemory<byte> metadata,
        Func<AssetChunk, CancellationToken, ValueTask<Stream>> open, long byteBudget = 32L * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(open);
        cancellationToken.ThrowIfCancellationRequested();
        if (metadata.Length < HeaderBytes + 32 || metadata.Length > MaximumMetadataBytes
            || !metadata.Span[..8].SequenceEqual("SIALMST1"u8))
            throw new InvalidDataException("Invalid lightmap stream metadata header or budget.");
        var bytes = metadata.Span;
        if (!SHA256.HashData(bytes[..^32]).AsSpan().SequenceEqual(bytes[^32..])
            || !ManifestIdentity(bytes[..^32]).AsSpan().SequenceEqual(bytes.Slice(BakeIdentityOffset, 32)))
            throw new InvalidDataException("Lightmap stream checksum or bake manifest mismatch.");
        using var input = new MemoryStream(bytes[..^32].ToArray(), false);
        using var reader = new BinaryReader(input);
        input.Position = 8;
        try {
            var resolution = reader.ReadInt32(); var receiverCount = reader.ReadInt32(); var chartCount = reader.ReadInt32();
            var pageCount = reader.ReadInt32(); var manifestBytes = reader.ReadInt32();
            if (resolution is < 8 or > 8192 || (resolution & (resolution - 1)) != 0
                || receiverCount is < 1 or > 1_000_000 || chartCount is < 1 or > 1_000_000
                || pageCount is < 1 or > AssetChunkManifest.MaximumChunks || manifestBytes is < 16 or > AssetChunkManifest.MaximumBytes
                || HeaderBytes + receiverCount * 80L + chartCount * 36L + pageCount * 20L + manifestBytes != input.Length)
                throw new InvalidDataException("Invalid lightmap stream section lengths.");
            var scene = reader.ReadBytes(32); var surface = reader.ReadBytes(32); var bake = reader.ReadBytes(32);
            float3 Vector() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var sky = new ProceduralSky { Horizon = Vector(), Zenith = Vector(), Ground = Vector(),
                SunDirection = Vector(), SunRadiance = Vector(), SunExponent = reader.ReadSingle(), Intensity = reader.ReadSingle() };
            var settings = new PbrLightmapBakeSettings { Sky = sky, TowardLight = Vector(), LightRadiance = Vector(),
                Samples = reader.ReadInt32(), MaximumDistance = reader.ReadSingle(), RayBias = reader.ReadSingle() };
            settings.Validate();
            var receivers = new PbrLightmapReceiver[receiverCount];
            var scales = new float4[receiverCount * 4];
            for (var i = 0; i < receivers.Length; i++) {
                receivers[i] = new(-1, reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), default);
                for (var c = 0; c < 4; c++) scales[i * 4 + c] = new(Vector(), reader.ReadSingle());
            }
            receivers = PbrLightmapAsset.ValidateReceivers(resolution, receivers);
            if (receivers.Any(r => r.Resolution > 1024 || (r.Resolution & (r.Resolution - 1)) != 0))
                throw new InvalidDataException("Streamed receiver dimensions must be powers of two within 8..1024.");
            PbrLightmapAsset.ValidateDecodeScales(scales);
            var charts = new PbrLightmapChart[chartCount];
            for (var i = 0; i < charts.Length; i++) charts[i] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            charts = PbrLightmapAsset.ValidateCharts(resolution, receivers, charts);
            var coarse = reader.ReadBytes(chartCount * 16);
            PbrLightmapAsset.ValidateUnorm8Data(coarse);
            for (var i = 0; i < chartCount; i++) if (coarse[i * 16 + 3] != 255)
                throw new InvalidDataException("Every streamed chart needs valid coarse illumination, including valid black.");
            var descriptors = new (int Receiver, int Mip, int X, int Y, int Chunk)[pageCount];
            for (var i = 0; i < descriptors.Length; i++) descriptors[i] = (reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            var manifest = AssetChunkManifest.Decode(reader.ReadBytes(manifestBytes));
            if (!manifest.Bootstrap.IsEmpty || manifest.Chunks.Any(c => !c.Dependencies.IsEmpty
                || c.Length > SceneStreamBlock.MaximumEncodedLength(PageBytes + 16) || c.Length + 1L > byteBudget))
                throw new InvalidDataException("Invalid lightmap page manifest or cache reservation.");
            var maximumMips = ReceiverMaximumMips(resolution, receivers, charts);
            var used = new HashSet<int>(); var addresses = new HashSet<(int, int, int, int)>();
            var baseReceivers = new HashSet<int>(); var pages = new PbrLightmapPage[pageCount];
            for (var i = 0; i < pages.Length; i++) {
                var p = descriptors[i];
                if ((uint)p.Receiver >= receivers.Length || p.Mip < 0 || p.Mip > maximumMips[p.Receiver]
                    || (uint)p.Chunk >= manifest.Chunks.Length || p.X < 0 || p.Y < 0
                    || !addresses.Add((p.Receiver, p.Mip, p.X, p.Y)))
                    throw new InvalidDataException("Invalid or duplicate lightmap page address.");
                var side = ((receivers[p.Receiver].Resolution >> p.Mip) + PageResolution - 1) / PageResolution;
                if (p.X >= side || p.Y >= side) throw new InvalidDataException("Lightmap page is outside its receiver mip.");
                used.Add(p.Chunk);
                if (p.Mip == 0) baseReceivers.Add(p.Receiver);
                pages[i] = new(p.Receiver, p.Mip, p.X, p.Y, manifest.Chunks[p.Chunk]);
            }
            if (used.Count != manifest.Chunks.Length || baseReceivers.Count != receivers.Length)
                throw new InvalidDataException("Unused chunks or missing base receiver pages.");
            return Task.FromResult(new PbrLightmapStream(resolution, receivers, charts, pages, scene, surface, bake,
                settings, scales, coarse, new AssetChunkCache(open, byteBudget)));
        }
        catch (ArgumentException e) { throw new InvalidDataException("Invalid lightmap stream values.", e); }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated lightmap stream metadata.", e); }
    }

    /// <summary>Returns an owned decoded page; callers budget and release their decoded/upload staging independently.</summary>
    public async Task<ReadOnlyMemory<byte>> ReadPageAsync(int page, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)page >= Pages.Length) throw new ArgumentOutOfRangeException(nameof(page));
        using var lease = await _cache.AcquireAsync(Pages.Span[page].Chunk, cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var data = SceneStreamBlock.Decode(lease.Memory, PageBytes + 16);
        if (data.Length != PageBytes + 16 || !data.AsSpan(0, 8).SequenceEqual("SIALMPG1"u8)
            || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)) != PageResolution
            || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)) != PageBorder)
            throw new InvalidDataException("Invalid lightmap page payload header.");
        try { PbrLightmapAsset.ValidateUnorm8Data(data.AsSpan(16)); }
        catch (ArgumentException e) { throw new InvalidDataException("Invalid lightmap page coefficients.", e); }
        return data.AsMemory(16);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return _cache.DisposeAsync();
    }

    internal static int[] ReceiverMaximumMips(int resolution, PbrLightmapReceiver[] receivers, PbrLightmapChart[] charts)
    {
        var indices = receivers.Select((r, i) => (r.StaticInstance, i)).ToDictionary(r => r.StaticInstance, r => r.i);
        var result = new int[receivers.Length];
        foreach (var chart in charts) result[indices[chart.StaticInstance]] = System.Math.Max(result[indices[chart.StaticInstance]],
            BitOperations.TrailingZeroCount((uint)(resolution | chart.X | chart.Y | chart.Width | chart.Height)));
        return result;
    }

    private static byte[] ManifestIdentity(ReadOnlySpan<byte> body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIALMST1-L1UNORM8-CHARTMEAN-PAGE32-BORDER1"u8);
        hash.AppendData(body[..BakeIdentityOffset]);
        hash.AppendData(body[(BakeIdentityOffset + 32)..]);
        return hash.GetHashAndReset();
    }
}
