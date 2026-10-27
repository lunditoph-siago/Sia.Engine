using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshRasterData
{
    public void Save(Stream stream) => Save(stream, default);

    public void Save(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[137]; "SIAMSHR\0"u8.CopyTo(header);
        Put(header, 8, _vertices.Length); Put(header, 12, _sourceIndexCount); Put(header, 16, _indices.Length);
        Put(header, 20, _sections.Length); Put(header, 24, _lods.Length); header[28] = (byte)(HasBakeUvs ? 1 : 0);
        Put(header, 29, Bounds.Min.x); Put(header, 33, Bounds.Min.y); Put(header, 37, Bounds.Min.z);
        Put(header, 41, Bounds.Max.x); Put(header, 45, Bounds.Max.y); Put(header, 49, Bounds.Max.z);
        Convert.FromHexString(SourceHash).CopyTo(header[53..]); Convert.FromHexString(BuildKey).CopyTo(header[85..]);
        Put(header, 117, Settings.MaximumLods); Put(header, 121, Settings.MinimumTriangles);
        Put(header, 125, Settings.TriangleRatio); Put(header, 129, Settings.NormalWeight); Put(header, 133, Settings.UVWeight);
        stream.Write(header);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
        Span<byte> buffer = stackalloc byte[32 * 1024];
        var writer = new MeshDataIO.Writer(stream, hash, buffer, cancellationToken);
        foreach (var vertex in _vertices) writer.Vertex(vertex);
        foreach (var index in _indices) writer.UInt(index);
        foreach (var section in _sections) { writer.Int(section.FirstIndex); writer.Int(section.IndexCount); writer.Int(section.MaterialSlot); }
        foreach (var level in _lods) { writer.Int(level.FirstIndex); writer.Int(level.IndexCount); writer.Float(level.Error); }
        foreach (var offset in _lodOffsets) writer.Int(offset);
        foreach (var uv in _bakeUvs) { writer.Float(uv.x); writer.Float(uv.y); }
        writer.Flush(); cancellationToken.ThrowIfCancellationRequested(); stream.Write(hash.GetHashAndReset());
    }

    public static MeshRasterData Load(Stream stream, ulong maximumBytes = 128ul * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < 169) throw new InvalidDataException("Raster exceeds its byte budget.");
        Span<byte> header = stackalloc byte[137]; stream.ReadExactly(header);
        var vertices = Int(header, 8); var sourceCount = Int(header, 12); var indices = Int(header, 16); var sections = Int(header, 20); var lods = Int(header, 24); var flags = header[28];
        var settings = new MeshRasterBuildSettings(Int(header, 117), Int(header, 121), BitConverter.Int32BitsToSingle(Int(header, 125)),
            BitConverter.Int32BitsToSingle(Int(header, 129)), BitConverter.Int32BitsToSingle(Int(header, 133)));
        if (!header[..8].SequenceEqual("SIAMSHR\0"u8) || !settings.IsValid || flags > 1 || vertices <= 0
            || sourceCount <= 0 || sourceCount % 3 != 0 || indices < sourceCount || indices % 3 != 0
            || sections <= 0 || sections > sourceCount / 3 || lods < sections || (long)lods > (long)sections * settings.MaximumLods)
            throw new InvalidDataException("Invalid Raster LOD header.");
        var encoded = 169ul + (ulong)vertices * (flags == 0 ? 48ul : 56ul) + (ulong)indices * 4
            + (ulong)sections * 12 + (ulong)lods * 12 + (ulong)(sections + 1L) * 4;
        if (encoded > maximumBytes || OwnedBytes(vertices, indices, sections, lods, flags != 0) > maximumBytes
            || stream.CanSeek && stream.Length - stream.Position != (long)encoded - header.Length)
            throw new InvalidDataException("Raster LOD counts exceed encoded/owned bytes or available input.");
        var bounds = new Aabb(new(Float(header, 29), Float(header, 33), Float(header, 37)),
            new(Float(header, 41), Float(header, 45), Float(header, 49)));
        var sourceHash = Convert.ToHexString(header[53..85]);
        if (Convert.ToHexString(header[85..117]) != ComputeBuildKey(sourceHash, settings))
            throw new InvalidDataException("Raster build key does not match its source and LOD settings.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
        Span<byte> buffer = stackalloc byte[32 * 1024];
        var reader = new MeshDataIO.Reader(stream, hash, buffer, encoded - 169, cancellationToken);
        var vertexValues = new MeshVertex[vertices];
        for (var at = 0; at < vertexValues.Length; at++) {
            var p = reader.Vector(); var n = reader.Vector(); var uv = new float2(reader.Float(), reader.Float());
            vertexValues[at] = new(p, n, uv) { Tangent = new(reader.Float(), reader.Float(), reader.Float(), reader.Float()) };
        }
        var indexValues = reader.UIntArray(indices); var sectionValues = new MeshSection[sections];
        for (var at = 0; at < sections; at++) sectionValues[at] = new(reader.Int(), reader.Int(), reader.Int());
        var levelValues = new MeshRasterLod[lods];
        for (var at = 0; at < lods; at++) levelValues[at] = new(reader.Int(), reader.Int(), reader.Float());
        var offsets = new int[sections + 1]; for (var at = 0; at < offsets.Length; at++) offsets[at] = reader.Int();
        var bakeUvs = flags == 0 ? [] : new float2[vertices];
        for (var at = 0; at < bakeUvs.Length; at++) bakeUvs[at] = new(reader.Float(), reader.Float());
        Span<byte> digest = stackalloc byte[32]; stream.ReadExactly(digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, hash.GetHashAndReset()) || stream.ReadByte() != -1)
            throw new InvalidDataException("Raster checksum or payload length mismatch.");
        try {
            var original = indexValues.AsSpan(0, sourceCount);
            MeshSource.ValidateGeometry(vertexValues, original, sectionValues, bakeUvs, bounds);
            if (MeshSource.ComputeContentHash(vertexValues, original, sectionValues, bakeUvs, bounds, cancellationToken) != sourceHash) throw new InvalidDataException("Raster LOD0 differs from its canonical source revision.");
            return new(vertexValues, indexValues, sectionValues, bakeUvs, sourceCount, levelValues, offsets, bounds, sourceHash, settings);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or IndexOutOfRangeException) {
            throw new InvalidDataException("Invalid Raster LOD payload.", error);
        }
    }

    private static int Int(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static float Float(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
    private static void Put(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void Put(Span<byte> bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], value);
}
