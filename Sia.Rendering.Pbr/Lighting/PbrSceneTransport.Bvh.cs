using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    /// <summary>Builds an independent reference domain using a validated existing local geometry packet.</summary>
    public static SceneTraceData BuildReference(PbrBvhTrace reference, PbrBvhTrace live, SceneTraceData liveData,
        ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference); ArgumentNullException.ThrowIfNull(live); ArgumentNullException.ThrowIfNull(liveData);
        if (!liveData.IsInstanced || !liveData.Identity.Span.SequenceEqual(live.Scene.TransportIdentity.Span)
            || reference.Scene.Geometry.Length != live.Scene.Geometry.Length)
            throw new ArgumentException("Reference domain must share the validated live local geometry.");
        for (var at = 0; at < live.Scene.Geometry.Length; at++) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(live.Scene.Geometry[at], reference.Scene.Geometry[at]))
                throw new ArgumentException("Reference geometry differs from the acquired live products.");
        }
        if ((ulong)liveData.Geometry.Length * 16 + (ulong)reference.Scene.Materials.Length * 32 > maximumBytes)
            throw new ArgumentException("Reference geometry and surface shape exceed the transport budget.", nameof(maximumBytes));
        var surfaces = new float4[reference.Scene.Materials.Length * 2];
        for (var at = 0; at < reference.Scene.Materials.Length; at++) {
            cancellationToken.ThrowIfCancellationRequested();
            (surfaces[at * 2], surfaces[at * 2 + 1]) = Surface(reference.Scene.Materials[at]);
        }
        return liveData.WithTransport(reference.PackGpuInstances(maximumBytes, cancellationToken), surfaces,
            reference.Scene.TransportIdentity.Span, maximumBytes, cancellationToken);
    }

    /// <summary>Pure probe input preparation over acquired BVHs; geometry remains local and shared.</summary>
    public static SceneTraceData Build(PbrBvhScene scene, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
        => Build(PbrBvhTrace.Build(scene, cancellationToken: cancellationToken), maximumBytes, cancellationToken);

    /// <summary>Prepares current instance acceleration with one shared GPU geometry packet per acquired local BVH.</summary>
    public static SceneTraceData Build(PbrBvhTrace trace, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace); cancellationToken.ThrowIfCancellationRequested();
        var scene = trace.Scene;
        var surfaces = new float4[scene.Materials.Length * 2]; var opaque = false;
        for (var i = 0; i < scene.Materials.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested(); var material = scene.Materials[i];
            (surfaces[i * 2], surfaces[i * 2 + 1]) = Surface(material);
            opaque |= !material.AlphaBlend;
        }
        if (!opaque || scene.Instances.Length == 0) throw new InvalidOperationException("Probe transport requires opaque geometry instances.");
        return new(trace.PackGpuGeometry(maximumBytes, cancellationToken), trace.PackGpuInstances(maximumBytes, cancellationToken),
            surfaces, maximumBytes, scene.TransportIdentity.Span, cancellationToken);
    }
}
