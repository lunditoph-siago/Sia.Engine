using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrSceneData
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    /// <summary>Writes reference-only metadata to a borrowed stream in one pass.</summary>
    public void Save(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        var attribution = s_utf8.GetBytes(Attribution);
        Span<byte> header = stackalloc byte[48];
        "SIASCNR\0"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], _bindings.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], _instances.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], MaterialReferenceCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], attribution.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], EncodedBytes - (ulong)header.Length - 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..], Lights.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], Cameras.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (HasAuthoredLights ? 1u : 0) | (HasAuthoredCameras ? 2u : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); WriteBlock(stream, header, hash);
        Span<byte> block = stackalloc byte[84];
        foreach (var binding in _bindings) {
            cancellationToken.ThrowIfCancellationRequested();
            WriteId(block, binding.Mesh.Value.Value); BinaryPrimitives.WriteInt32LittleEndian(block[16..], binding.Materials.Length);
            WriteBlock(stream, block[..20], hash);
            foreach (var material in binding.Materials) {
                BinaryPrimitives.WriteInt32LittleEndian(block, material.Slot); WriteId(block[4..], material.Material.Value.Value);
                WriteBlock(stream, block[..20], hash);
            }
        }
        foreach (var instance in _instances) {
            cancellationToken.ThrowIfCancellationRequested();
            WriteId(block, instance.Id); BinaryPrimitives.WriteInt32LittleEndian(block[16..], instance.Binding);
            WriteMatrix(block[20..], instance.Transform); WriteBlock(stream, block, hash);
        }
        foreach (var light in Lights) {
            cancellationToken.ThrowIfCancellationRequested(); WriteLight(block, light); WriteBlock(stream, block[..76], hash);
        }
        foreach (var camera in Cameras) {
            cancellationToken.ThrowIfCancellationRequested(); WriteCamera(block, camera); WriteBlock(stream, block[..64], hash);
        }
        WriteBlock(stream, attribution, hash); cancellationToken.ThrowIfCancellationRequested(); stream.Write(hash.GetHashAndReset());
    }

    public static PbrSceneData Load(Stream stream, ulong maximumBytes = 128ul * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < 80) throw new InvalidDataException("Scene framing exceeds its decode limit.");
        try {
            Span<byte> header = stackalloc byte[48]; stream.ReadExactly(header);
            if (!header[..8].SequenceEqual("SIASCNR\0"u8)) throw new InvalidDataException("Invalid reference-scene signature.");
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
            if ((flags & ~3u) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(header[44..]) != 0)
                throw new InvalidDataException("Unknown reference-scene header flags.");
            var authored = (flags & 1u) != 0;
            var authoredCameras = (flags & 2u) != 0;
            var lightCount = BinaryPrimitives.ReadInt32LittleEndian(header[32..]);
            var cameraCount = BinaryPrimitives.ReadInt32LittleEndian(header[36..]);
            var bindingCount = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            var instanceCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            var slotCount = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
            var attributionLength = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
            if (bindingCount is < 0 or > MaximumBindings || instanceCount is < 0 or > MaximumInstances
                || slotCount is < 0 or > MaximumMaterialReferences || attributionLength is < 0 or > MaximumAttributionBytes
                || lightCount is < 0 or > MaximumLights || cameraCount is < 0 or > MaximumCameras
                || !authored && lightCount != 0 || !authoredCameras && cameraCount != 0
                || slotCount < bindingCount || slotCount > (long)bindingCount * Mesh.MeshManifest.MaximumMaterialSlots)
                throw new InvalidDataException("Invalid reference-scene metadata counts.");
            var length = (ulong)bindingCount * 20 + (ulong)slotCount * 20 + (ulong)instanceCount * 84 + (ulong)lightCount * 76
                + (ulong)cameraCount * 64 + (ulong)attributionLength;
            if (length != BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) || length + (ulong)header.Length + 32 > maximumBytes
                || ArrayBytes(bindingCount, instanceCount, slotCount, lightCount, cameraCount) > maximumBytes)
                throw new InvalidDataException("Reference-scene metadata exceeds its declared size or decode limit.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
            var bindings = new PbrMeshBinding[bindingCount]; var remaining = slotCount; Span<byte> block = stackalloc byte[84];
            for (var i = 0; i < bindings.Length; i++) {
                cancellationToken.ThrowIfCancellationRequested(); ReadBlock(stream, block[..20], hash);
                var mesh = new AssetRefer<Mesh.MeshManifestRecord>.Id(new(ReadId(block)));
                var count = BinaryPrimitives.ReadInt32LittleEndian(block[16..]);
                if (count is < 1 or > Mesh.MeshManifest.MaximumMaterialSlots || count > remaining)
                    throw new InvalidDataException("Invalid scene Mesh material-slot count.");
                remaining -= count;
                var materials = new PbrMaterialSlot[count];
                for (var slot = 0; slot < count; slot++) {
                    ReadBlock(stream, block[..20], hash);
                    materials[slot] = new(BinaryPrimitives.ReadInt32LittleEndian(block), new(new(ReadId(block[4..]))));
                }
                bindings[i] = PbrMeshBinding.Adopt(mesh, materials);
            }
            if (remaining != 0) throw new InvalidDataException("Scene material-reference count mismatch.");
            var instances = new PbrMeshInstance[instanceCount];
            for (var i = 0; i < instances.Length; i++) {
                cancellationToken.ThrowIfCancellationRequested(); ReadBlock(stream, block, hash);
                instances[i] = new(ReadId(block), BinaryPrimitives.ReadInt32LittleEndian(block[16..]), ReadMatrix(block[20..]));
            }
            var lights = authored ? new PbrSceneLight[lightCount] : null;
            if (lights is not null) for (var i = 0; i < lights.Length; i++) {
                cancellationToken.ThrowIfCancellationRequested(); ReadBlock(stream, block[..76], hash); lights[i] = ReadLight(block);
            }
            var cameras = authoredCameras ? new PbrSceneCamera[cameraCount] : null;
            if (cameras is not null) for (var i = 0; i < cameras.Length; i++) {
                cancellationToken.ThrowIfCancellationRequested(); ReadBlock(stream, block[..64], hash); cameras[i] = ReadCamera(block);
            }
            var attribution = new byte[attributionLength]; ReadBlock(stream, attribution, hash);
            Span<byte> digest = stackalloc byte[32]; stream.ReadExactly(digest); cancellationToken.ThrowIfCancellationRequested();
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), digest)) throw new InvalidDataException("Reference-scene checksum mismatch.");
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing reference-scene data.");
            return new(bindings, instances, s_utf8.GetString(attribution), lights, cameras);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated reference-scene metadata.", error); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid reference-scene metadata.", error); }
    }

    private static void WriteBlock(Stream stream, ReadOnlySpan<byte> bytes, IncrementalHash hash) { hash.AppendData(bytes); stream.Write(bytes); }
    private static void ReadBlock(Stream stream, Span<byte> bytes, IncrementalHash hash) { stream.ReadExactly(bytes); hash.AppendData(bytes); }
    private static void WriteId(Span<byte> bytes, Guid id) => id.TryWriteBytes(bytes, bigEndian: true, out _);
    private static Guid ReadId(ReadOnlySpan<byte> bytes) => new(bytes[..16], bigEndian: true);
    private static void WriteLight(Span<byte> bytes, PbrSceneLight light)
    {
        WriteId(bytes, light.Id); BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], (int)light.Kind);
        WriteVector(bytes[20..], light.Color); BinaryPrimitives.WriteSingleLittleEndian(bytes[32..], light.Intensity);
        WriteVector(bytes[36..], light.Position); WriteVector(bytes[48..], light.Direction);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[60..], light.Range);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[64..], light.InnerAngle);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[68..], light.OuterAngle);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[72..], light.CastShadows ? 1u : 0);
    }
    private static PbrSceneLight ReadLight(ReadOnlySpan<byte> bytes)
    {
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[72..]);
        if (flags > 1) throw new InvalidDataException("Unknown authored-light flags.");
        return new(ReadId(bytes), (PbrSceneLightKind)BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]),
            ReadVector(bytes[20..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[32..]), ReadVector(bytes[36..]), ReadVector(bytes[48..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[60..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[64..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[68..]), flags != 0);
    }
    private static void WriteCamera(Span<byte> bytes, PbrSceneCamera camera)
    {
        WriteId(bytes, camera.Id); WriteVector(bytes[16..], camera.Position); WriteColumn(bytes[28..], camera.Rotation.value);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[44..], camera.FovRadians);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[48..], camera.Near);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[52..], camera.Far);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[56..], (int)camera.FovAxis);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[60..], 0);
    }
    private static PbrSceneCamera ReadCamera(ReadOnlySpan<byte> bytes)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..]) != 0)
            throw new InvalidDataException("Unknown authored-camera flags.");
        return new(ReadId(bytes), ReadVector(bytes[16..]), new quaternion(ReadColumn(bytes[28..])),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[44..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[48..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[52..]), (PbrCameraFovAxis)BinaryPrimitives.ReadInt32LittleEndian(bytes[56..]));
    }
    private static void WriteVector(Span<byte> bytes, float3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, v.x); BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], v.y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], v.z);
    }
    private static float3 ReadVector(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadSingleLittleEndian(b),
        BinaryPrimitives.ReadSingleLittleEndian(b[4..]), BinaryPrimitives.ReadSingleLittleEndian(b[8..]));
    private static void WriteColumn(Span<byte> bytes, float4 c)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, c.x); BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], c.y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], c.z); BinaryPrimitives.WriteSingleLittleEndian(bytes[12..], c.w);
    }
    private static float4 ReadColumn(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadSingleLittleEndian(b), BinaryPrimitives.ReadSingleLittleEndian(b[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(b[8..]), BinaryPrimitives.ReadSingleLittleEndian(b[12..]));
    private static void WriteMatrix(Span<byte> bytes, float4x4 t) { WriteColumn(bytes, t.c0); WriteColumn(bytes[16..], t.c1); WriteColumn(bytes[32..], t.c2); WriteColumn(bytes[48..], t.c3); }
    private static float4x4 ReadMatrix(ReadOnlySpan<byte> b) => new(ReadColumn(b), ReadColumn(b[16..]), ReadColumn(b[32..]), ReadColumn(b[48..]));
}
