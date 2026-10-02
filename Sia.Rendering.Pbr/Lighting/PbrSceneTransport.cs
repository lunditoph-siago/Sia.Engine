using Sia.Math;
using System.Runtime.InteropServices;
using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    public static SceneTraceData Build(PbrSceneAsset scene, ulong maximumBytes = 128ul * 1024 * 1024, bool finest = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        long capacity = 0;
        foreach (var instance in scene.Instances.Span) {
            if (scene.Materials.Span[instance.Material].AlphaBlend) continue;
            var tree = scene.Geometry.Span[instance.Geometry].Build.Tree;
            foreach (var node in tree.Nodes.Span[..(finest ? tree.Nodes.Length : tree.RootCount)]) {
                if (finest ? node.ChildCount != 0 : node.Parent >= 0) continue;
                capacity = checked(capacity + node.TriangleCount);
            }
        }
        if (capacity == 0 || capacity > 4_000_000 || ((ulong)capacity * 16) + 32 > maximumBytes)
            throw new InvalidOperationException("Scene transport exceeds its configured build budget or triangle limit.");
        var identity = Identity(scene, finest);
        var triangles = GC.AllocateUninitializedArray<SceneTraceTriangle>((int)capacity);
        var count = 0;
        foreach (var instance in scene.Instances.Span) {
            var material = scene.Materials.Span[instance.Material];
            if (material.AlphaBlend) continue; // no alpha-mask representation exists yet
            var tree = scene.Geometry.Span[instance.Geometry].Build.Tree;
            var albedo = material.Parameters.BaseColor * Average(material.BaseColor);
            var metal = material.Parameters.Metallic * Average(material.MetallicRoughness).z;
            albedo *= 1 - MathF.Min(1, MathF.Max(0, metal));
            var emission = material.Parameters.EmissiveColor * material.Parameters.EmissiveStrength * Average(material.Emissive);
            foreach (var node in tree.Nodes.Span[..(finest ? tree.Nodes.Length : tree.RootCount)]) {
                if (finest ? node.ChildCount != 0 : node.Parent >= 0) continue;
                var indices = tree.Indices.Slice(node.TriangleOffset * 3, node.TriangleCount * 3);
                for (var i = 0; i < indices.Length; i += 3) {
                    var a = math.mul(instance.Transform, new float4(tree.Vertices[(int)indices[i]].Position, 1)).xyz;
                    var b = math.mul(instance.Transform, new float4(tree.Vertices[(int)indices[i + 1]].Position, 1)).xyz;
                    var c = math.mul(instance.Transform, new float4(tree.Vertices[(int)indices[i + 2]].Position, 1)).xyz;
                    if (math.lengthsq(math.cross(b - a, c - a)) < 1e-16f) continue;
                    triangles[count++] = new(a, b, c, albedo, emission, material.DoubleSided);
                }
            }
        }
        return new(triangles.AsSpan(0, count), maximumBytes, identity);
    }

    public static byte[] Identity(PbrSceneAsset scene, bool finest = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var hash = new SceneIdentityHash();
        void Record(float4 v)
        {
            Span<float4> one = [v];
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(one));
        }
        Record(new(scene.Geometry.Length, scene.Materials.Length, scene.Instances.Length, finest ? 1 : 0));
        Span<float4> positions = stackalloc float4[256];
        foreach (var mesh in scene.Geometry.Span) {
            var tree = mesh.Build.Tree;
            if (finest) {
                AppendFinestIdentity(hash, tree, positions);
                continue;
            }
            var vertices = tree.Vertices;
            var indices = tree.Indices;
            Record(new(vertices.Length, indices.Length, tree.RootCount, 0));
            for (var offset = 0; offset < vertices.Length; offset += positions.Length) {
                var count = System.Math.Min(positions.Length, vertices.Length - offset);
                for (var i = 0; i < count; i++)
                    positions[i] = new(vertices[offset + i].Position, 0);
                hash.AppendData(MemoryMarshal.AsBytes(positions[..count]));
            }
            foreach (var node in tree.Nodes.Span[..tree.RootCount])
                hash.AppendData(MemoryMarshal.AsBytes(indices.Slice(node.TriangleOffset * 3, node.TriangleCount * 3)));
        }
        foreach (var material in scene.Materials.Span) {
            var metal = material.Parameters.Metallic * Average(material.MetallicRoughness).z;
            Record(new(material.Parameters.BaseColor * Average(material.BaseColor) * (1 - MathF.Min(1, MathF.Max(0, metal))), material.DoubleSided ? 1 : 0));
            Record(new(material.Parameters.EmissiveColor * material.Parameters.EmissiveStrength * Average(material.Emissive), material.AlphaBlend ? 1 : 0));
        }
        foreach (var instance in scene.Instances.Span) {
            Record(new(instance.Geometry, instance.Material, 0, 0));
            Record(instance.Transform.c0);
            Record(instance.Transform.c1);
            Record(instance.Transform.c2);
            Record(instance.Transform.c3);
        }
        return hash.Finish();
    }

    private static float3 Average(PbrTextureData? texture)
    {
        if (texture is null) return new(1);
        var bytes = texture.MipLevels.Span[^1].Span;
        var sum = float3.zero;
        float Decode(byte value)
        {
            var x = value / 255f;
            return texture.Srgb ? x <= .04045f ? x / 12.92f : MathF.Pow((x + .055f) / 1.055f, 2.4f) : x;
        }
        for (var i = 0; i < bytes.Length; i += 4)
            sum += new float3(Decode(bytes[i]), Decode(bytes[i + 1]), Decode(bytes[i + 2]));
        return sum / (bytes.Length / 4);
    }

    public static DiffuseProbeAsset CreateVolume(SceneTraceData scene, uint3 dimensions,
        ProceduralSky sky, Aabb? bounds = null)
    {
        var box = bounds ?? scene.Bounds;
        var extent = math.max(box.Max - box.Min, new float3(.01f));
        if (dimensions.x > DiffuseProbeAsset.MaximumProbes || dimensions.y > DiffuseProbeAsset.MaximumProbes
            || dimensions.z > DiffuseProbeAsset.MaximumProbes)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        var count = (ulong)dimensions.x * dimensions.y * dimensions.z;
        if (dimensions.x < 2 || dimensions.y < 2 || dimensions.z < 2 || count > DiffuseProbeAsset.MaximumProbes)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        var step = extent / new float3(dimensions.x - 1, dimensions.y - 1, dimensions.z - 1);
        var sh = IrradianceSh.Project(sky.Evaluate);
        var coefficients = new float4[(int)count * 9];
        for (var i = 0; i < coefficients.Length; i++)
            coefficients[i] = new(sh[i % 9].xyz, 1);
        return new(box.Min, step, dimensions, scene.Identity.Span, coefficients);
    }
}
