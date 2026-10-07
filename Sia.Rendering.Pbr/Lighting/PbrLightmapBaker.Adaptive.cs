using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrLightmapBaker
{
    /// <summary>
    /// Picks the first covered chart layout within each geometry's resolution bounds,
    /// packs variable instance tiles, and integrates one receiver at a time against one BVH.
    /// WorkingBytes bounds tile surface/filter/Float coefficients, not scene/cooker/BVH scratch.
    /// AssetBytes bounds the complete encoded asset, including per-receiver decode scales.
    /// </summary>
    public static PbrLightmapAsset BakeAdaptive(PbrSceneAsset scene, out PbrSceneAsset mappedScene,
        PbrLightmapBakeSettings? settings = null, int minimumReceiverResolution = 16, int maximumReceiverResolution = 1024,
        int padding = 2, int maximumAtlasResolution = 4096, ulong maximumAssetBytes = 32ul * 1024 * 1024,
        ulong maximumWorkingBytes = 128ul * 1024 * 1024, ulong maximumTraceBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default, PbrLightmapEncoding encoding = PbrLightmapEncoding.L1Half)
    {
        var options = settings ?? new();
        options.Validate();
        var prepared = PbrLightmapInput.PrepareAdaptive(scene, minimumReceiverResolution, maximumReceiverResolution,
            padding, maximumAtlasResolution, maximumAssetBytes, maximumWorkingBytes, cancellationToken, encoding);
        var data = encoding == PbrLightmapEncoding.L1Half ? new Half[prepared.Resolution * prepared.Resolution * 16] : [];
        var quantized = encoding == PbrLightmapEncoding.L1Unorm8 ? new byte[prepared.Resolution * prepared.Resolution * 16] : [];
        var scales = encoding == PbrLightmapEncoding.L1Unorm8 ? new float4[prepared.Receivers.Length * 4] : [];
        var tracing = PbrSceneTransport.BuildStatic(prepared.Scene, maximumTraceBytes, finest: true).Packed.ToArray();
        var directions = Directions(options);
        for (var i = 0; i < prepared.Receivers.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            IntegrateReceiver(prepared, prepared.Receivers[i], options, tracing, directions, data, quantized, scales, i, cancellationToken);
        }
        var identity = PbrLightmapAsset.Identity(prepared.Scene);
        var asset = encoding == PbrLightmapEncoding.L1Half
            ? PbrLightmapAsset.FromHalf(prepared.Resolution, prepared.Receivers, identity, prepared.Identity, options, data, prepared.Charts)
            : PbrLightmapAsset.FromUnorm8(prepared.Resolution, prepared.Receivers, identity, prepared.Identity, options, quantized, scales, prepared.Charts);
        mappedScene = prepared.Scene;
        return asset;
    }

    // Keep tile arrays in a separate frame so they are collectible between receivers.
    // The output Half atlas and immutable BVH remain shared for the entire bake.
    private static void IntegrateReceiver(PbrLightmapInput.Prepared prepared, PbrLightmapReceiver receiver,
        PbrLightmapBakeSettings options, float4[] tracing, float3[] directions, Half[] destination,
        byte[] quantized, float4[] scales, int receiverIndex,
        CancellationToken cancellationToken)
    {
        var input = PbrLightmapInput.ReceiverTile(prepared, receiver, cancellationToken);
        var coefficients = Integrate(input, options, tracing, directions, cancellationToken);
        if (quantized.Length != 0) {
            // Match conversion of the Half reference without retaining a Half atlas.
            float4 Read(int pixel, int band) {
                var v = coefficients[pixel * 4 + band];
                if (!math.all(math.isfinite(v)) || math.any(math.abs(v) > new float4(65504)))
                    throw new InvalidOperationException("Surface radiance is not representable by the reference lightmap encoding.");
                return new((float)(Half)v.x, (float)(Half)v.y, (float)(Half)v.z, (float)(Half)v.w);
            }
            PbrLightmapAsset.QuantizeReceiver(receiver, prepared.Resolution, quantized,
                scales.AsSpan(receiverIndex * 4, 4), Read, cancellationToken);
            return;
        }
        for (var y = 0; y < receiver.Resolution; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < receiver.Resolution; x++) {
                var source = (y * receiver.Resolution + x) * 4;
                var target = ((receiver.Y + y) * prepared.Resolution + receiver.X + x) * 16;
                for (var c = 0; c < 4; c++) {
                    var v = coefficients[source + c];
                    if (!math.all(math.isfinite(v)) || math.any(math.abs(v) > new float4(65504)))
                        throw new InvalidOperationException("Surface radiance is not representable by the Half lightmap encoding.");
                    destination[target + c * 4] = (Half)v.x;
                    destination[target + c * 4 + 1] = (Half)v.y;
                    destination[target + c * 4 + 2] = (Half)v.z;
                    destination[target + c * 4 + 3] = (Half)v.w;
                }
            }
        }
    }
}
