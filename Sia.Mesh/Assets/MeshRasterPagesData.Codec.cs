using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed partial class MeshRasterPagesData
{
    private const int HeaderBytes = 272;

    public void Save(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[HeaderBytes]; WriteHeader(header, false); stream.Write(header);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
        WriteBody(stream, hash, cancellationToken); cancellationToken.ThrowIfCancellationRequested(); stream.Write(hash.GetHashAndReset());
    }

    private string ComputeBuildKey(CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("SIARASTERPAGES1"u8);
        Span<byte> header = stackalloc byte[HeaderBytes]; WriteHeader(header, true); hash.AppendData(header);
        WriteBody(Stream.Null, hash, token); return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void WriteHeader(Span<byte> header, bool clearKey)
    {
        "SIAMSHRI"u8.CopyTo(header); ProductId.Value.TryWriteBytes(header.Slice(8, 16), bigEndian: true, out _);
        Raster.Id.Value.TryWriteBytes(header.Slice(24, 16), bigEndian: true, out _);
        Convert.FromHexString(SourceHash).CopyTo(header[40..]); Convert.FromHexString(Raster.BuildKey).CopyTo(header[72..]);
        Convert.FromHexString(RasterContentHash).CopyTo(header[104..]);
        if (clearKey) header.Slice(136, 32).Clear(); else Convert.FromHexString(BuildKey).CopyTo(header[136..]);
        Put(header, 168, Bounds.Min.x); Put(header, 172, Bounds.Min.y); Put(header, 176, Bounds.Min.z);
        Put(header, 180, Bounds.Max.x); Put(header, 184, Bounds.Max.y); Put(header, 188, Bounds.Max.z);
        Put(header, 192, SourceVertexCount); Put(header, 196, SourceTriangleCount); Put(header, 200, TotalIndexCount);
        Put(header, 204, HasBakeUvs ? 1 : 0);
        Put(header, 208, Settings.MaximumLods); Put(header, 212, Settings.MinimumTriangles); Put(header, 216, Settings.TriangleRatio);
        Put(header, 220, Settings.NormalWeight); Put(header, 224, Settings.UVWeight);
        Put(header, 228, MeshRasterPageData.MaximumVertices); Put(header, 232, MeshRasterPageData.MaximumTriangles);
        Put(header, 236, _sections.Length); Put(header, 240, _lods.Length); Put(header, 244, VertexPageCount);
        Put(header, 248, _pages.Length); Put(header, 252, _vertexDependencies.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[256..], RasterEncodedBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(header[264..], EncodedBytes - HeaderBytes - 32);
    }

    private void WriteBody(Stream stream, IncrementalHash hash, CancellationToken token)
    {
        Span<byte> buffer = stackalloc byte[32 * 1024]; var writer = new MeshDataIO.Writer(stream, hash, buffer, token);
        foreach (var section in _sections) {
            token.ThrowIfCancellationRequested(); writer.Int(section.MaterialSlot); writer.Int(section.FirstIndex); writer.Int(section.IndexCount);
            writer.Int(section.FirstLod); writer.Int(section.LodCount); writer.Box(section.Bounds);
        }
        foreach (var lod in _lods) {
            token.ThrowIfCancellationRequested(); writer.Int(lod.FirstIndex); writer.Int(lod.IndexCount); writer.Float(lod.Error); writer.Int(lod.FirstPage); writer.Int(lod.PageCount);
        }
        Span<byte> identity = stackalloc byte[16];
        foreach (var page in _pages) {
            token.ThrowIfCancellationRequested(); page.Id.Value.TryWriteBytes(identity, bigEndian: true, out _); writer.Bytes(identity);
            writer.Bytes(Convert.FromHexString(page.ContentHash)); writer.Int(page.EncodedBytes); writer.Int((int)page.Kind);
            writer.Int(page.FirstElement); writer.Int(page.ElementCount); writer.Int(page.FirstDependency); writer.Int(page.DependencyCount);
        }
        foreach (var dependency in _vertexDependencies) writer.Int(dependency);
        writer.Flush();
    }

    public static MeshRasterPagesData Load(Stream stream, ulong maximumBytes = MaximumEncodedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < HeaderBytes + 32) throw new InvalidDataException("Raster index framing exceeds its byte budget.");
        try {
            Span<byte> header = stackalloc byte[HeaderBytes]; stream.ReadExactly(header);
            Require(header[..8].SequenceEqual("SIAMSHRI"u8), "Invalid exact Raster index signature.");
            var sections = Int(header, 236); var lods = Int(header, 240); var vertexPages = Int(header, 244);
            var pages = Int(header, 248); var dependencies = Int(header, 252); var flags = Int(header, 204);
            RequireCounts(sections, lods, pages, dependencies, vertexPages);
            Require(flags is 0 or 1 && Int(header, 228) == MeshRasterPageData.MaximumVertices && Int(header, 232) == MeshRasterPageData.MaximumTriangles,
                "Unsupported exact Raster index flags or page policy.");
            var encoded = WireBytes(sections, lods, pages, dependencies);
            Require(encoded <= maximumBytes && encoded <= MaximumEncodedBytes && OwnedBytes(sections, lods, pages, dependencies) <= maximumBytes
                && BinaryPrimitives.ReadUInt64LittleEndian(header[264..]) == encoded - HeaderBytes - 32
                && (!stream.CanSeek || stream.Length - stream.Position == (long)encoded - HeaderBytes), "Raster index exceeds its count/encoded/known-array limits or length.");
            var product = new AssetId(new Guid(header.Slice(8, 16), bigEndian: true)); var rasterId = new AssetId(new Guid(header.Slice(24, 16), bigEndian: true));
            var sourceHash = Convert.ToHexString(header.Slice(40, 32)); var rasterKey = Convert.ToHexString(header.Slice(72, 32));
            var rasterHash = Convert.ToHexString(header.Slice(104, 32)); var expectedKey = Convert.ToHexString(header.Slice(136, 32));
            var settings = new MeshRasterBuildSettings(Int(header, 208), Int(header, 212), Float(header, 216), Float(header, 220), Float(header, 224));
            var bounds = new Aabb(new(Float(header, 168), Float(header, 172), Float(header, 176)), new(Float(header, 180), Float(header, 184), Float(header, 188)));
            Require(product.IsValid && rasterId.IsValid && product != rasterId && settings.IsValid && Valid(bounds)
                && Int(header, 192) > 0 && Int(header, 196) > 0 && (long)Int(header, 196) * 3 <= Int(header, 200)
                && Int(header, 200) % 3 == 0 && rasterKey == MeshRasterData.ComputeBuildKey(sourceHash, settings), "Invalid exact Raster index source/derivation summary.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
            Span<byte> buffer = stackalloc byte[32 * 1024];
            var reader = new MeshDataIO.Reader(stream, hash, buffer, encoded - HeaderBytes - 32, cancellationToken);
            var sectionValues = new MeshRasterPagesSection[sections]; var lodValues = new MeshRasterPagesLod[lods];
            var pageValues = new MeshRasterPageDescriptor[pages]; var dependencyValues = new int[dependencies];
            for (var index = 0; index < sections; index++) sectionValues[index] = new(reader.Int(), reader.Int(), reader.Int(), reader.Int(), reader.Int(), reader.Box());
            for (var index = 0; index < lods; index++) lodValues[index] = new(reader.Int(), reader.Int(), reader.Float(), reader.Int(), reader.Int());
            Span<byte> identity = stackalloc byte[16]; Span<byte> digest = stackalloc byte[32];
            for (var index = 0; index < pages; index++) {
                reader.Bytes(identity); reader.Bytes(digest);
                pageValues[index] = new(new(new Guid(identity, bigEndian: true)), Convert.ToHexString(digest), reader.Int(),
                    (MeshRasterPageKind)reader.Int(), reader.Int(), reader.Int(), reader.Int(), reader.Int());
            }
            for (var index = 0; index < dependencies; index++) dependencyValues[index] = reader.Int();
            cancellationToken.ThrowIfCancellationRequested(); stream.ReadExactly(digest);
            Require(CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), digest), "Raster index checksum mismatch.");
            cancellationToken.ThrowIfCancellationRequested(); Require(stream.ReadByte() == -1, "Trailing Raster index data.");
            var result = new MeshRasterPagesData(product, new(rasterId, rasterKey), sourceHash, rasterHash,
                BinaryPrimitives.ReadUInt64LittleEndian(header[256..]), settings, bounds, Int(header, 192), Int(header, 196), Int(header, 200),
                flags != 0, vertexPages, sectionValues, lodValues, pageValues, dependencyValues, cancellationToken);
            Require(result.BuildKey == expectedKey, "Raster index exact directory build-key mismatch."); return result;
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated Raster page index.", error); }
        catch (Exception error) when (error is ArgumentException or OverflowException or IndexOutOfRangeException) {
            throw new InvalidDataException("Invalid Raster page index.", error);
        }
    }

    private static int Int(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static float Float(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
    private static void Put(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], value);
    private static void Put(Span<byte> bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], value);
}
