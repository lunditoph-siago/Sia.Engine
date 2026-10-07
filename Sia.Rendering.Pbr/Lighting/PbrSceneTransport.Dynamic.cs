namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    internal static ulong DynamicMaximumBytes(PbrSceneAsset scene)
    {
        var triangles = 0;
        foreach (var instance in scene.Instances.Span) {
            if (!instance.Dynamic || scene.Materials.Span[instance.Material].AlphaBlend) continue;
            var tree = scene.Geometry.Span[instance.Geometry].Build.Tree;
            foreach (var root in tree.Nodes.Span[..tree.RootCount])
                triangles = checked(triangles + root.TriangleCount);
        }
        return triangles == 0 ? 0 : SceneTraceData.MaximumPackedBytes(triangles);
    }

    // Live records use authored geometry/material indices; static bake identity is never recomputed here.
    internal static SceneTraceData? BuildDynamic(PbrSceneAsset source, ReadOnlySpan<PbrSceneInstance> live,
        ulong maximumBytes)
    {
        if (live.IsEmpty) return null;
        foreach (var instance in live)
            if (!instance.Dynamic || source.Materials.Span[instance.Material].AlphaBlend)
                throw new ArgumentException("Dynamic transport accepts enabled opaque dynamic records only.", nameof(live));
        return BuildCore(source, live, maximumBytes, false, default);
    }
}
