using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneAsset
{
    private const int HeaderSize = 56;

    public byte[] Encode(CancellationToken cancellationToken = default)
    {
        var textures = new List<PbrTextureData>();
        var textureIndices = new Dictionary<PbrTextureData, int>(ReferenceEqualityComparer.Instance);
        foreach (var material in Materials.Span) {
            foreach (var texture in Maps(material)) {
                if (texture is not null && textureIndices.TryAdd(texture, textures.Count)) { textures.Add(texture); }
            }
        }
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write(Attribution);
            writer.Write(Geometry.Length);
            foreach (var geometry in Geometry.Span) {
                cancellationToken.ThrowIfCancellationRequested();
                Blob(writer, geometry.EncodeCompressed(cancellationToken));
            }
            writer.Write(textures.Count);
            foreach (var texture in textures) {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(texture.Width); writer.Write(texture.Height); writer.Write(texture.Srgb);
                var sampler = texture.Sampler;
                writer.Write((uint)sampler.AddressU); writer.Write((uint)sampler.AddressV);
                writer.Write((uint)sampler.MinFilter); writer.Write((uint)sampler.MagFilter); writer.Write((uint)sampler.MipFilter);
                writer.Write(sampler.UseMipmaps); writer.Write(texture.MipLevels.Length);
                foreach (var level in texture.MipLevels.Span) { Blob(writer, level.Span); }
            }
            writer.Write(Materials.Length);
            foreach (var material in Materials.Span) {
                var p = material.Parameters;
                Vector(writer, p.BaseColor); writer.Write(p.Metallic); writer.Write(p.Roughness);
                Vector(writer, p.EmissiveColor); writer.Write(p.EmissiveStrength);
                writer.Write(material.NormalScale); writer.Write(material.OcclusionStrength);
                foreach (var map in Maps(material)) { writer.Write(map is null ? -1 : textureIndices[map]); }
            }
            writer.Write(Instances.Length);
            foreach (var instance in Instances.Span) {
                writer.Write(instance.Geometry); writer.Write(instance.Material);
                Column(writer, instance.Transform.c0); Column(writer, instance.Transform.c1);
                Column(writer, instance.Transform.c2); Column(writer, instance.Transform.c3);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var raw = payload.ToArray();
        using var output = new MemoryStream();
        output.Write(new byte[HeaderSize]);
        using (var compressed = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) { compressed.Write(raw); }
        var result = output.ToArray();
        "SIAPBR01"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), raw.Length);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(16), result.Length);
        SHA256.HashData(raw).CopyTo(result, 24);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public static PbrSceneAsset Decode(ReadOnlySpan<byte> bytes, int maximumDecodedBytes = 128 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDecodedBytes);
        Require(bytes.Length > HeaderSize && bytes[..8].SequenceEqual("SIAPBR01"u8), "Invalid PBR scene header.");
        Require(BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) == FormatVersion, "Unsupported PBR scene version.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        Require(length >= 13 && length <= maximumDecodedBytes && BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]) == bytes.Length,
            "Invalid PBR scene length or decoded byte budget.");
        var raw = new byte[length];
        using (var input = new MemoryStream(bytes[HeaderSize..].ToArray(), writable: false)) {
            using var compressed = new GZipStream(input, CompressionMode.Decompress);
            try { compressed.ReadExactly(raw); }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated PBR scene payload.", error); }
            Require(compressed.ReadByte() == -1, "PBR scene exceeds its declared decoded length.");
        }
        Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), bytes.Slice(24, 32)), "PBR scene checksum mismatch.");
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(raw, writable: false);
        using var reader = new BinaryReader(stream);
        try {
            var attribution = reader.ReadString();
            Require(attribution.Length <= 16384, "PBR scene attribution is too long.");
            var geometry = new MeshPatchAsset[Count(reader, 4, 4096)];
            var remaining = maximumDecodedBytes - raw.Length;
            for (var i = 0; i < geometry.Length; i++) {
                var blob = Blob(reader);
                var decoded = blob.AsSpan().StartsWith("SIAGZIP1"u8) && blob.Length >= 24
                    ? BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(8)) : blob.Length;
                Require(decoded >= 0 && decoded <= remaining, "PBR geometry exceeds the aggregate decoded byte budget.");
                geometry[i] = MeshPatchAsset.Decode(blob, cancellationToken, remaining);
                remaining -= (int)decoded;
            }
            var textures = new PbrTextureData[Count(reader, 34, 4096)];
            for (var i = 0; i < textures.Length; i++) {
                cancellationToken.ThrowIfCancellationRequested();
                var width = reader.ReadUInt32(); var height = reader.ReadUInt32(); var srgb = reader.ReadBoolean();
                var sampler = new PbrTextureSampler((WGPUAddressMode)reader.ReadUInt32(), (WGPUAddressMode)reader.ReadUInt32(),
                    (WGPUFilterMode)reader.ReadUInt32(), (WGPUFilterMode)reader.ReadUInt32(), (WGPUMipmapFilterMode)reader.ReadUInt32(), reader.ReadBoolean());
                var levels = new ReadOnlyMemory<byte>[Count(reader, 4, 14)];
                for (var level = 0; level < levels.Length; level++) { levels[level] = Blob(reader); }
                textures[i] = PbrTextureData.Create(width, height, srgb, levels, sampler);
            }
            var materials = new PbrMaterialAsset[Count(reader, 64, 4096)];
            for (var i = 0; i < materials.Length; i++) {
                var parameters = new PbrMaterial(Vector(reader), reader.ReadSingle(), reader.ReadSingle(), Vector(reader), reader.ReadSingle());
                var normalScale = reader.ReadSingle(); var occlusionStrength = reader.ReadSingle();
                materials[i] = new(parameters, Texture(), Texture(), Texture(), Texture(), Texture(), normalScale, occlusionStrength);
            }
            var instances = new PbrSceneInstance[Count(reader, 72, 1000000)];
            for (var i = 0; i < instances.Length; i++) {
                instances[i] = new(reader.ReadInt32(), reader.ReadInt32(), new float4x4(Column(reader), Column(reader), Column(reader), Column(reader)));
            }
            Require(stream.Position == stream.Length, "Unexpected data after PBR scene records.");
            return Create(geometry, materials, instances, attribution);

            PbrTextureData? Texture()
            {
                var index = reader.ReadInt32();
                Require(index == -1 || (uint)index < (uint)textures.Length, "Invalid PBR texture reference.");
                return index < 0 ? null : textures[index];
            }
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated PBR scene records.", error); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid PBR scene records.", error); }
    }

    private static PbrTextureData?[] Maps(PbrMaterialAsset material) =>
        [material.BaseColor, material.Normal, material.MetallicRoughness, material.Occlusion, material.Emissive];

    private static int Count(BinaryReader reader, int stride, int maximum)
    {
        var count = reader.ReadInt32();
        Require(count >= 0 && count <= maximum && (long)count * stride <= reader.BaseStream.Length - reader.BaseStream.Position,
            "Invalid PBR scene record count.");
        return count;
    }

    private static void Blob(BinaryWriter writer, ReadOnlySpan<byte> bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    private static byte[] Blob(BinaryReader reader) => reader.ReadBytes(Count(reader, 1, int.MaxValue));
    private static void Vector(BinaryWriter writer, float3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
    private static float3 Vector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static void Column(BinaryWriter writer, float4 value) { Vector(writer, value.xyz); writer.Write(value.w); }
    private static float4 Column(BinaryReader reader) => new(Vector(reader), reader.ReadSingle());
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidDataException(message); } }
}
