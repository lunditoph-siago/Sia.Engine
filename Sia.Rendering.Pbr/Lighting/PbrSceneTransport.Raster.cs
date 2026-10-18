using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    public static SceneTraceData Build(PbrRasterScene scene, ulong maximumBytes = 128ul * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(scene);
        long capacity = 0;
        foreach (var instance in scene.Instances.Span) {
            if (!scene.Materials.Span[instance.Material].AlphaBlend)
                capacity = checked(capacity + scene.Geometry.Span[instance.Geometry].Indices.Length / 3);
        }
        if (capacity == 0 || capacity > 4_000_000 || (ulong)capacity * 16 + 32 > maximumBytes)
            throw new InvalidOperationException("Raster scene transport exceeds its build budget or triangle limit.");
        var triangles = GC.AllocateUninitializedArray<SceneTraceTriangle>((int)capacity); var count = 0;
        foreach (var instance in scene.Instances.Span) {
            var material = scene.Materials.Span[instance.Material]; if (material.AlphaBlend) continue;
            var geometry = scene.Geometry.Span[instance.Geometry]; var indices = geometry.Indices;
            var vertices = geometry.Product.Vertices;
            var metal = material.Parameters.Metallic * Average(material.MetallicRoughness).z;
            var albedo = material.Parameters.BaseColor * Average(material.BaseColor) * (1 - MathF.Min(1, MathF.Max(0, metal)));
            var emission = material.Parameters.EmissiveColor * material.Parameters.EmissiveStrength * Average(material.Emissive);
            for (var i = 0; i < indices.Length; i += 3) {
                var a = math.mul(instance.Transform, new float4(vertices[(int)indices[i]].Position, 1)).xyz;
                var b = math.mul(instance.Transform, new float4(vertices[(int)indices[i + 1]].Position, 1)).xyz;
                var c = math.mul(instance.Transform, new float4(vertices[(int)indices[i + 2]].Position, 1)).xyz;
                if (math.lengthsq(math.cross(b - a, c - a)) < 1e-16f) continue;
                triangles[count++] = new(a, b, c, albedo, emission, material.DoubleSided);
            }
        }
        return new(triangles.AsSpan(0, count), maximumBytes, Identity(scene));
    }

    public static byte[] Identity(PbrRasterScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!scene.TransportIdentity.IsEmpty) return scene.TransportIdentity.ToArray();
        var hash = new SceneIdentityHash(); hash.AppendData("SIAPBRRASTERTRACE1"u8);
        void Record(float4 value) { Span<float4> one = [value]; hash.AppendData(MemoryMarshal.AsBytes(one)); }
        Record(new(scene.Geometry.Length, scene.Materials.Length, scene.Instances.Length, 0));
        Span<float4> positions = stackalloc float4[256];
        foreach (var geometry in scene.Geometry.Span) {
            // Hash referenced triangle positions, so unused product vertices and shading attributes cannot invalidate a bake.
            Record(new(geometry.Indices.Length, 0, 0, 0));
            var buffered = 0;
            foreach (var index in geometry.Indices) {
                positions[buffered++] = new(geometry.Product.Vertices[(int)index].Position, 0);
                if (buffered != positions.Length) continue;
                hash.AppendData(MemoryMarshal.AsBytes(positions)); buffered = 0;
            }
            hash.AppendData(MemoryMarshal.AsBytes(positions[..buffered]));
        }
        foreach (var material in scene.Materials.Span) {
            var metal = material.Parameters.Metallic * Average(material.MetallicRoughness).z;
            Record(new(material.Parameters.BaseColor * Average(material.BaseColor) * (1 - MathF.Min(1, MathF.Max(0, metal))), material.DoubleSided ? 1 : 0));
            Record(new(material.Parameters.EmissiveColor * material.Parameters.EmissiveStrength * Average(material.Emissive), material.AlphaBlend ? 1 : 0));
        }
        foreach (var instance in scene.Instances.Span) {
            Record(new(instance.Geometry, instance.Material, 0, 0));
            Record(instance.Transform.c0); Record(instance.Transform.c1); Record(instance.Transform.c2); Record(instance.Transform.c3);
        }
        return hash.Finish();
    }
}
