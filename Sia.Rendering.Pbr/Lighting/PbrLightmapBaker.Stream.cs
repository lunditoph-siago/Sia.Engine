using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrLightmapBaker
{
    /// <summary>
    /// Bakes and writes one compact receiver at a time, without allocating a full output atlas.
    /// The caller owns chunk destinations; returned metadata is published only after every write succeeds.
    /// WorkingBytes bounds principal surface/filter/Float/packed tile arrays, not scene/cook/BVH/metadata/GC peak.
    /// </summary>
    public static async Task<(PbrSceneAsset Scene, byte[] Metadata)> BakeStreamAsync(PbrSceneAsset scene,
        Func<AssetChunk, ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        PbrLightmapBakeSettings? settings = null, int minimumReceiverResolution = 16, int maximumReceiverResolution = 1024,
        int padding = 2, int maximumAtlasResolution = 8192, int maximumMetadataBytes = PbrLightmapStream.MaximumMetadataBytes,
        ulong maximumWorkingBytes = 160ul * 1024 * 1024, ulong maximumTraceBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (maximumReceiverResolution > 1024) throw new ArgumentOutOfRangeException(nameof(maximumReceiverResolution));
        if (maximumMetadataBytes is < 0 or > PbrLightmapStream.MaximumMetadataBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumMetadataBytes));
        var options = settings ?? new(); options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        // This bounds virtual address space only. PrepareAdaptive does not allocate its output atlas.
        var prepared = PbrLightmapInput.PrepareAdaptive(scene, minimumReceiverResolution, maximumReceiverResolution,
            padding, maximumAtlasResolution, int.MaxValue, maximumWorkingBytes, cancellationToken,
            PbrLightmapEncoding.L1Unorm8, maximumMetadataBytes);
        var identity = PbrLightmapAsset.Identity(prepared.Scene);
        var charts = prepared.Charts.GroupBy(c => c.StaticInstance).ToDictionary(g => g.Key, g => g.ToArray());
        float4[]? tracing = null;
        var directions = Directions(options);
        PbrLightmapAsset Tile(int index) {
            // Admission/metadata validation in CookTilesAsync precedes BVH acquisition.
            tracing ??= PbrSceneTransport.BuildStatic(prepared.Scene, maximumTraceBytes, finest: true).Packed.ToArray();
            var receiver = prepared.Receivers[index];
            return IntegrateStreamTile(prepared, receiver, charts[receiver.StaticInstance], options,
                identity, tracing, directions, cancellationToken);
        }
        var metadata = await PbrLightmapStream.CookTilesAsync(prepared.Resolution, prepared.Receivers, prepared.Charts,
            identity, prepared.Identity, options, Tile, write, maximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        return (prepared.Scene, metadata);
    }

    // Surface/filter/Float arrays leave this frame before chunk IO awaits. Only the
    // current packed receiver and the shared immutable trace data survive those awaits.
    private static PbrLightmapAsset IntegrateStreamTile(PbrLightmapInput.Prepared prepared, PbrLightmapReceiver receiver,
        PbrLightmapChart[] charts, PbrLightmapBakeSettings options, byte[] identity, float4[] tracing, float3[] directions,
        CancellationToken token)
    {
        var input = PbrLightmapInput.ReceiverTile(prepared, receiver, token);
        var coefficients = Integrate(input, options, tracing, directions, token);
        var local = receiver with { X = 0, Y = 0, ScaleBias = new(1, 1, 0, 0) };
        var data = new byte[receiver.Resolution * receiver.Resolution * 16]; var scales = new float4[4];
        float4 Read(int pixel, int band) {
            var v = coefficients[pixel * 4 + band];
            if (!math.all(math.isfinite(v)) || math.any(math.abs(v) > new float4(65504)))
                throw new InvalidOperationException("Surface radiance is not representable by the reference lightmap encoding.");
            return new((float)(Half)v.x, (float)(Half)v.y, (float)(Half)v.z, (float)(Half)v.w);
        }
        PbrLightmapAsset.QuantizeReceiver(local, receiver.Resolution, data, scales, Read, token);
        return PbrLightmapAsset.FromUnorm8(receiver.Resolution, [local], identity, prepared.Identity, options, data, scales,
            charts.Select(c => c with { X = c.X - receiver.X, Y = c.Y - receiver.Y }).ToArray());
    }
}
