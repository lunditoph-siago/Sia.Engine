using System.Runtime.InteropServices;
using Sia.Engine.Mesh;
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
        var blocks = new List<TransportBlock>();
        var addresses = new List<TriangleAddress>(capacity);
        foreach (var instance in scene.Instances.Span) {
            var m = scene.Bootstrap.Materials.Span[instance.MaterialIndex];
            var surface = Surface(m);
            var tree = scene.Hierarchies[instance.AssetIndex];
            foreach (var node in tree.Nodes.AsSpan(0, tree.Roots))
                foreach (var part in node.Pages) {
                    var page = scene.ResidentRoots[part.Id];
                    AddBlock(new(page, null, part.First, part.Count, instance.Transform, surface));
                }
        }
        foreach (var instance in scene.Bootstrap.Instances.Span) {
            var m = scene.Bootstrap.Materials.Span[instance.Material];
            if ((staticOnly && instance.Dynamic) || m.AlphaBlend) continue;
            var surface = Surface(m);
            var tree = scene.Bootstrap.Geometry.Span[instance.Geometry].Build.Tree;
            foreach (var node in tree.Nodes.Span[..tree.RootCount]) {
                AddBlock(new(null, tree, node.TriangleOffset, node.TriangleCount, instance.Transform, surface));
            }
        }
        return SceneTraceData.Create(new StreamTriangles(CollectionsMarshal.AsSpan(blocks), CollectionsMarshal.AsSpan(addresses)),
            maximumBytes, identity.Span);

        void AddBlock(TransportBlock block)
        {
            var index = blocks.Count;
            blocks.Add(block);
            for (var i = 0; i < block.Count; i++) {
                var triangle = block.Read(i);
                if (math.lengthsq(math.cross(triangle.B - triangle.A, triangle.C - triangle.A)) < 1e-16f) continue;
                if (((ulong)(addresses.Count + 1) * 16) + 32 > maximumBytes)
                    throw new InvalidOperationException(block.Page is null
                        ? "Conventional stream GI proxy exceeds its build budget."
                        : "Stream GI proxy exceeds its build budget.");
                addresses.Add(new(index, i));
            }
        }
    }

    private readonly record struct TriangleAddress(int Block, int Triangle);

    private readonly record struct TransportBlock(StreamGeometryPage? Page, MeshPatchTree? Tree,
        int First, int Count, float4x4 Transform, TransportSurface Surface)
    {
        public SceneTraceTriangle Read(int triangle) => Page is { } page
            ? Triangle<float4, PackedPosition>(page.Vertices, page.Indices, (First + triangle) * 3, Transform, Surface)
            : Triangle<MeshVertex, MeshPosition>(Tree!.Vertices, Tree.Indices, (First + triangle) * 3, Transform, Surface);
    }

    private readonly ref struct StreamTriangles(ReadOnlySpan<TransportBlock> blocks,
        ReadOnlySpan<TriangleAddress> addresses) : ISceneTraceTriangleSource
    {
        private readonly ReadOnlySpan<TransportBlock> _blocks = blocks;
        private readonly ReadOnlySpan<TriangleAddress> _addresses = addresses;
        public int Count => _addresses.Length;
        public SceneTraceTriangle this[int index] {
            get {
                var address = _addresses[index];
                return _blocks[address.Block].Read(address.Triangle);
            }
        }
    }
}
