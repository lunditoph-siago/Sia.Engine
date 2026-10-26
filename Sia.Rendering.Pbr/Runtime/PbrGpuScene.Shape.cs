using System.Runtime.CompilerServices;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrGpuScene
{
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
        foreach (var instance in source.Instances.Span) {
            var tree = source.Hierarchies[instance.AssetIndex];
            var ids = tree.Nodes.Take(tree.Roots).SelectMany(node => node.Pages).Select(page => page.Id).Distinct(StringComparer.Ordinal);
            foreach (var id in ids) {
                if (instanced && !roots.Add(source.ResidentRoots[id].Data?.ContentHash ?? id)) continue;
                var page = source.PageTable[id]; vertices += (uint)page.Vertices; triangles += (uint)page.Triangles;
            }
        }
        var instanceBytes = instanced ? (ulong)System.Math.Max(1, source.Instances.Length + source.TransparentInstances.Length)
            * (ulong)Unsafe.SizeOf<PbrInstanceGpu>() : 0;
        return (vertices, triangles, instanceBytes);
    }
}
