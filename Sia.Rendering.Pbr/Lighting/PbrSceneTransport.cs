using Sia.Math;
using System.Runtime.InteropServices;
using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    internal static (float4 Albedo, float4 Emission) Surface(PbrMaterialAsset material)
    {
        float3 diffuse;
        if (material.DiffuseMoments is { } moments) diffuse = moments.Diffuse(material);
        else {
            var metal = material.Parameters.Metallic * Average(material.MetallicRoughness).z;
            diffuse = material.Parameters.BaseColor * Average(material.BaseColor) * (1 - MathF.Min(1, MathF.Max(0, metal)));
        }
        return (new(diffuse, material.DoubleSided ? 1 : 0),
            new(material.Parameters.EmissiveColor * material.Parameters.EmissiveStrength * Average(material.Emissive), material.AlphaBlend ? 1 : 0));
    }

    internal static byte[] ReferenceIdentity(PbrSceneData scene, Func<PbrMeshBinding, MeshManifest> mesh,
        Func<PbrMaterialSlot, PbrMaterialAsset> material, CancellationToken token)
    {
        // Source revisions are deliberately conservative: authored normal/UV changes may invalidate a bake.
        // Per-binding cache avoids repeatedly averaging texture pixels for repeated instances.
        var bindings = new byte[scene.Bindings.Length][];
        return ReferenceIdentity(scene.Instances, at => {
            if (bindings[at] is not { } source) {
                var binding = scene.Bindings[at];
                source = ReferenceBindingIdentity(mesh(binding).SourceHash, binding.Materials.Length,
                    index => (binding.Materials[index].Slot, material(binding.Materials[index])), token);
                bindings[at] = source;
            }
            return source;
        }, token);
    }

    internal static byte[] ReferenceBindingIdentity(string sourceHash, int count,
        Func<int, (int Slot, PbrMaterialAsset Material)> material, CancellationToken token)
    {
        var revision = new SceneIdentityHash(); revision.AppendData(Convert.FromHexString(sourceHash)); Count(revision, count);
        for (var at = 0; at < count; at++) {
            token.ThrowIfCancellationRequested(); var slot = material(at); Count(revision, slot.Slot);
            var surface = Surface(slot.Material); Record(revision, surface.Albedo); Record(revision, surface.Emission);
        }
        return revision.Finish();
    }

    internal static byte[] ReferenceIdentity(ReadOnlySpan<PbrMeshInstance> instances, Func<int, byte[]> binding, CancellationToken token)
    {
        var hash = new SceneIdentityHash(); hash.AppendData("SIAPBRREFERENCETRACE"u8);
        Span<byte> id = stackalloc byte[16]; Count(hash, instances.Length);
        foreach (var instance in instances) {
            token.ThrowIfCancellationRequested();
            instance.Id.TryWriteBytes(id, bigEndian: true, out _); hash.AppendData(id); hash.AppendData(binding(instance.Binding));
            Record(hash, instance.Transform.c0); Record(hash, instance.Transform.c1); Record(hash, instance.Transform.c2); Record(hash, instance.Transform.c3);
        }
        return hash.Finish();
    }

    private static void Record(SceneIdentityHash target, float4 value)
    { Span<float4> row = [value]; target.AppendData(MemoryMarshal.AsBytes(row)); }
    private static void Count(SceneIdentityHash target, int value)
    { Span<byte> bytes = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value); target.AppendData(bytes); }

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
        _ = VolumeCount(dimensions);
        return CreateVolume(scene, dimensions, IrradianceSh.Project(sky.Evaluate), bounds);
    }

    /// <summary>Seeds physical incident-radiance SH without reprojecting a procedural placeholder.</summary>
    public static DiffuseProbeAsset CreateVolume(SceneTraceData scene, uint3 dimensions,
        ReadOnlySpan<float4> coefficients, Aabb? bounds = null)
    {
        if (coefficients.Length != 9) throw new ArgumentException("Probe seeds require nine physical SH coefficients.", nameof(coefficients));
        foreach (var coefficient in coefficients)
            if (!math.all(math.isfinite(coefficient))) throw new ArgumentException("Non-finite probe seed.", nameof(coefficients));
        var box = bounds ?? scene.Bounds;
        var extent = math.max(box.Max - box.Min, new float3(.01f));
        var count = VolumeCount(dimensions);
        var step = extent / new float3(dimensions.x - 1, dimensions.y - 1, dimensions.z - 1);
        var seed = new float4[count * 9];
        for (var i = 0; i < seed.Length; i++)
            seed[i] = new(coefficients[i % 9].xyz, 1);
        return new(box.Min, step, dimensions, scene.Identity.Span, seed);
    }

    private static int VolumeCount(uint3 dimensions)
    {
        if (dimensions.x > DiffuseProbeAsset.MaximumProbes || dimensions.y > DiffuseProbeAsset.MaximumProbes
            || dimensions.z > DiffuseProbeAsset.MaximumProbes)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        var count = (ulong)dimensions.x * dimensions.y * dimensions.z;
        if (dimensions.x < 2 || dimensions.y < 2 || dimensions.z < 2 || count > DiffuseProbeAsset.MaximumProbes)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        return (int)count;
    }
}
