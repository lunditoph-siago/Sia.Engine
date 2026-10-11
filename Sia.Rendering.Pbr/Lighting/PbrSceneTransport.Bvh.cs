using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    /// <summary>Pure probe input preparation over acquired BVHs; geometry remains local and shared.</summary>
    public static SceneTraceData Build(PbrBvhScene scene, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene); cancellationToken.ThrowIfCancellationRequested();
        var surfaces = new float4[scene.Materials.Length * 2]; var opaque = false;
        for (var i = 0; i < scene.Materials.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested(); var material = scene.Materials[i];
            (surfaces[i * 2], surfaces[i * 2 + 1]) = Surface(material);
            opaque |= !material.AlphaBlend;
        }
        if (!opaque || scene.Instances.Length == 0) throw new InvalidOperationException("Probe transport requires opaque geometry instances.");
        var trace = PbrBvhTrace.Build(scene, cancellationToken: cancellationToken);
        return new(trace.PackGpuGeometry(maximumBytes, cancellationToken), trace.PackGpuInstances(maximumBytes, cancellationToken),
            surfaces, maximumBytes, scene.TransportIdentity.Span, cancellationToken);
    }
}
