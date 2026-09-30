using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    public static SceneTraceData Build(PbrSceneAsset scene, ulong maximumBytes = 128ul * 1024 * 1024, bool finest = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var triangles = new List<SceneTraceTriangle>();
        var meshes = scene.Geometry.ToArray().Select(g => {
            var tree = g.Build.Tree;
            if (finest) return tree.CopyFinestGeometry().Geometry;
            var geometry = tree.CopyGeometry().Geometry;
            var indices = new List<uint>();
            foreach (var root in tree.Nodes.Span[..tree.RootCount])
                indices.AddRange(geometry.Indices.AsSpan(root.TriangleOffset * 3, root.TriangleCount * 3));
            return geometry with { Indices = [.. indices] };
        }).ToArray();
        foreach (var instance in scene.Instances.Span) {
            var material = scene.Materials.Span[instance.Material];
            if (material.AlphaBlend) continue; // no alpha-mask representation exists yet
            var mesh = meshes[instance.Geometry];
            var albedo = material.Parameters.BaseColor * Average(material.BaseColor);
            var metal = material.Parameters.Metallic * Average(material.MetallicRoughness).z;
            albedo *= 1 - MathF.Min(1, MathF.Max(0, metal));
            var emission = material.Parameters.EmissiveColor * material.Parameters.EmissiveStrength * Average(material.Emissive);
            for (var i = 0; i < mesh.Indices.Length; i += 3) {
                float3 Position(uint index) => math.mul(instance.Transform, new float4(mesh.Vertices[index].Position, 1)).xyz;
                var a = Position(mesh.Indices[i]);
                var b = Position(mesh.Indices[i + 1]);
                var c = Position(mesh.Indices[i + 2]);
                if (math.lengthsq(math.cross(b - a, c - a)) < 1e-16f) continue;
                if (checked(((ulong)(triangles.Count + 1) * 16) + 32) > maximumBytes)
                    throw new InvalidOperationException("Scene transport exceeds its configured build budget.");
                triangles.Add(new(a, b, c, albedo, emission, material.DoubleSided));
            }
        }
        return new(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(triangles), maximumBytes, Identity(scene, finest));
    }

    public static byte[] Identity(PbrSceneAsset scene, bool finest = false)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Record(float4 v)
        {
            Span<float4> one = [v];
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(one));
        }
        Record(new(scene.Geometry.Length, scene.Materials.Length, scene.Instances.Length, finest ? 1 : 0));
        foreach (var mesh in scene.Geometry.Span) {
            var tree = mesh.Build.Tree;
            var geometry = finest ? tree.CopyFinestGeometry().Geometry : tree.CopyGeometry().Geometry;
            Record(new(geometry.Vertices.Length, geometry.Indices.Length, tree.RootCount, 0));
            foreach (var vertex in geometry.Vertices)
                Record(new(vertex.Position, 0));
            if (finest)
                hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(geometry.Indices.AsSpan()));
            else
                foreach (var node in tree.Nodes.Span[..tree.RootCount])
                    hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(geometry.Indices.AsSpan(node.TriangleOffset * 3, node.TriangleCount * 3)));
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
        return hash.GetHashAndReset();
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
