using System.Runtime.CompilerServices;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrGpuScene
{
    internal static ulong StreamStorageBytes(ulong vertices, ulong triangles)
        => checked((((System.Math.Max(1ul, vertices) * StreamGeometryPage.VertexBytes) + 15) & ~15ul)
            + System.Math.Max(16ul, System.Math.Max(1ul, triangles) * 12));

    // Same referenced shape used by graphics admission and CPU verification; no GPU effects.
    internal static (ulong Vertices, ulong Triangles, ulong Instances) RootShape(PbrSceneStream source, bool instanced)
    {
        ArgumentNullException.ThrowIfNull(source);
        ulong vertices = 0, triangles = 0;
        var resident = new HashSet<int>();
        foreach (var instance in source.TransparentInstances) {
            if (instanced && !resident.Add(instance.Geometry)) continue;
            using var geometry = new PbrResidentGeometry(source.TransparentGeometry[instance.Geometry]);
            vertices += (uint)geometry.VertexCount; triangles += geometry.TriangleCount;
        }
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var instanceRoots = new HashSet<int>();
        foreach (var instance in source.Instances.Span) {
            var tree = source.Hierarchies[instance.AssetIndex];
            instanceRoots.Clear();
            foreach (var node in tree.Nodes[..tree.Roots])
                foreach (var part in tree.GetPages(node)) {
                    if (!instanceRoots.Add(part.Page)) continue;
                    var page = source.Pages[part.Page];
                    if (instanced && !roots.Add(source.ResidentRoots[page.Id].Data?.ContentHash ?? page.Id)) continue;
                    vertices += (uint)page.Vertices; triangles += (uint)page.Triangles;
                }
        }
        var instanceBytes = instanced ? (ulong)System.Math.Max(1, source.Instances.Length + source.TransparentInstances.Length)
            * (ulong)Unsafe.SizeOf<PbrInstanceGpu>() : 0;
        return (vertices, triangles, instanceBytes);
    }
}
