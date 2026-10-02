using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    public static SceneTraceData Build(PbrSceneStream scene, ulong maximumBytes = 128ul * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(scene);
        long maximumTriangles = 0;
        foreach (var instance in scene.Instances.Span) {
            var tree = scene.Hierarchies[instance.AssetIndex];
            foreach (var node in tree.Nodes.AsSpan(0, tree.Roots))
                maximumTriangles = checked(maximumTriangles + node.Triangles);
        }
        var capacity = (int)System.Math.Min((ulong)maximumTriangles, System.Math.Min(4_000_000ul, maximumBytes / 16));
        var triangles = new List<SceneTraceTriangle>(capacity);
        foreach (var instance in scene.Instances.Span) {
            var m = scene.Bootstrap.Materials.Span[instance.MaterialIndex];
            var metal = m.Parameters.Metallic * Average(m.MetallicRoughness).z;
            var albedo = m.Parameters.BaseColor * Average(m.BaseColor) * (1 - System.Math.Clamp(metal, 0, 1));
            var emission = m.Parameters.EmissiveColor * m.Parameters.EmissiveStrength * Average(m.Emissive);
            var tree = scene.Hierarchies[instance.AssetIndex];
            foreach (var node in tree.Nodes.Take(tree.Roots))
                foreach (var part in node.Pages) {
                    var page = scene.ResidentRoots[part.Id];
                    var indices = page.Indices.Slice(part.First * 3, part.Count * 3);
                    for (var i = 0; i < indices.Length; i += 3) {
                        var a = math.mul(instance.Transform, new float4(page.Vertices[(int)indices[i]].xyz, 1)).xyz;
                        var b = math.mul(instance.Transform, new float4(page.Vertices[(int)indices[i + 1]].xyz, 1)).xyz;
                        var c = math.mul(instance.Transform, new float4(page.Vertices[(int)indices[i + 2]].xyz, 1)).xyz;
                        if (math.lengthsq(math.cross(b - a, c - a)) < 1e-16f) continue;
                        if (((ulong)(triangles.Count + 1) * 16) + 32 > maximumBytes)
                            throw new InvalidOperationException("Stream GI proxy exceeds its build budget.");
                        triangles.Add(new(a, b, c, albedo, emission, m.DoubleSided));
                    }
                }
        }
        return new(CollectionsMarshal.AsSpan(triangles), maximumBytes, scene.Identity.Span);
    }
}
