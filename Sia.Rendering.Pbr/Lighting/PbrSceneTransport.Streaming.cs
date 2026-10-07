using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    public static SceneTraceData Build(PbrSceneStream scene, ulong maximumBytes = 128ul * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return BuildStreamCore(scene, maximumBytes, scene.Identity, false);
    }

    public static SceneTraceData BuildStatic(PbrSceneStream scene, ulong maximumBytes = 128ul * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.StaticIdentity.IsEmpty)
            throw new NotSupportedException("Canonical static transport requires versioned source metadata; recook the legacy stream.");
        return BuildStreamCore(scene, maximumBytes, scene.StaticIdentity, true);
    }

    private static SceneTraceData BuildStreamCore(PbrSceneStream scene, ulong maximumBytes, ReadOnlyMemory<byte> identity, bool staticOnly)
    {
        ArgumentNullException.ThrowIfNull(scene);
        long maximumTriangles = 0;
        foreach (var instance in scene.Instances.Span) {
            var tree = scene.Hierarchies[instance.AssetIndex];
            foreach (var node in tree.Nodes.AsSpan(0, tree.Roots))
                maximumTriangles = checked(maximumTriangles + node.Triangles);
        }
        foreach (var instance in scene.Bootstrap.Instances.Span) {
            if ((staticOnly && instance.Dynamic) || scene.Bootstrap.Materials.Span[instance.Material].AlphaBlend) continue;
            var tree = scene.Bootstrap.Geometry.Span[instance.Geometry].Build.Tree;
            foreach (var node in tree.Nodes.Span[..tree.RootCount])
                maximumTriangles = checked(maximumTriangles + node.TriangleCount);
        }
        var capacity = (int)System.Math.Min((ulong)maximumTriangles, System.Math.Min(4_000_000ul, maximumBytes / 16));
        var triangles = new List<SceneTraceTriangle>(capacity);
        foreach (var instance in scene.Instances.Span) {
            var m = scene.Bootstrap.Materials.Span[instance.MaterialIndex];
            var surface = Surface(m);
            var tree = scene.Hierarchies[instance.AssetIndex];
            foreach (var node in tree.Nodes.AsSpan(0, tree.Roots))
                foreach (var part in node.Pages) {
                    var page = scene.ResidentRoots[part.Id];
                    var indices = page.Indices.Slice(part.First * 3, part.Count * 3);
                    foreach (var triangle in Triangles(page.Vertices, indices, instance.Transform, surface)) {
                        if (((ulong)(triangles.Count + 1) * 16) + 32 > maximumBytes)
                            throw new InvalidOperationException("Stream GI proxy exceeds its build budget.");
                        triangles.Add(triangle);
                    }
                }
        }
        foreach (var instance in scene.Bootstrap.Instances.Span) {
            var m = scene.Bootstrap.Materials.Span[instance.Material];
            if ((staticOnly && instance.Dynamic) || m.AlphaBlend) continue;
            var surface = Surface(m);
            var tree = scene.Bootstrap.Geometry.Span[instance.Geometry].Build.Tree;
            foreach (var node in tree.Nodes.Span[..tree.RootCount]) {
                var indices = tree.Indices.Slice(node.TriangleOffset * 3, node.TriangleCount * 3);
                foreach (var triangle in Triangles(tree.Vertices, indices, instance.Transform, surface)) {
                    if (((ulong)(triangles.Count + 1) * 16) + 32 > maximumBytes)
                        throw new InvalidOperationException("Conventional stream GI proxy exceeds its build budget.");
                    triangles.Add(triangle);
                }
            }
        }
        return new(CollectionsMarshal.AsSpan(triangles), maximumBytes, identity.Span);
    }
}
