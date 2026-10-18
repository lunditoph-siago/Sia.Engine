#if !BROWSER
using System.Buffers.Binary;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal static partial class LightmapCook
{
    private static IEnumerable<PbrLightmapTile> Tiles(int resolution, int firstMip)
    {
        for (var level = firstMip; (resolution >> level) >= 2; level++) {
            var dimension = resolution >> level; var edge = System.Math.Min(64, dimension);
            for (var y = 0; y < dimension; y += edge)
                for (var x = 0; x < dimension; x += edge) yield return new(level, x, y, edge);
        }
    }

    private static int CountTiles(int resolution, out ulong rawBytes, out ulong encodedBytes)
    {
        var count = 0; rawBytes = encodedBytes = 0;
        for (var dimension = resolution; dimension >= 2; dimension >>= 1) {
            var edge = System.Math.Min(64, dimension); var pages = dimension / edge; pages *= pages;
            count += pages; rawBytes += (ulong)dimension * (ulong)dimension * 49;
            encodedBytes += (ulong)pages * (92ul + (ulong)edge * (ulong)edge * 25);
        }
        return count;
    }

    private static int TileSlot(int resolution, int firstMip, PbrLightmapTile tile)
    {
        var first = 0;
        for (var level = firstMip; (resolution >> level) >= 2; level++) {
            var dimension = resolution >> level; var edge = System.Math.Min(64, dimension); var columns = dimension / edge;
            if (tile.Level == level) {
                if (tile.Edge != edge || tile.X < 0 || tile.Y < 0 || tile.X >= dimension || tile.Y >= dimension || tile.X % edge != 0 || tile.Y % edge != 0)
                    throw new InvalidDataException("Producer emitted an invalid lightmap tile.");
                return first + tile.Y / edge * columns + tile.X / edge;
            }
            first += columns * columns;
        }
        throw new InvalidDataException("Producer emitted an invalid mip level.");
    }

    // Private scratch format: twelve little-endian floats and coverage, without chart/area data.
    // Only this task-owned spool is read, then removed before atomic publication.
    private static void WriteRaw(Stream stream, ReadOnlySpan<PbrLightmapFilterTexel> values, CancellationToken token)
    {
        Span<byte> buffer = stackalloc byte[49 * 256];
        for (var offset = 0; offset < values.Length; offset += 256) {
            token.ThrowIfCancellationRequested(); var count = System.Math.Min(256, values.Length - offset);
            for (var at = 0; at < count; at++) {
                var sample = values[offset + at].Coefficients; var row = buffer.Slice(at * 49, 49);
                WriteVector(row, sample.Constant); WriteVector(row[12..], sample.Y);
                WriteVector(row[24..], sample.Z); WriteVector(row[36..], sample.X); row[48] = sample.Covered ? (byte)1 : (byte)0;
            }
            stream.Write(buffer[..(count * 49)]);
        }
    }

    private static void ReadRaw(Stream stream, Span<PbrLightmapCoefficients> values, CancellationToken token)
    {
        Span<byte> buffer = stackalloc byte[49 * 256];
        for (var offset = 0; offset < values.Length; offset += 256) {
            token.ThrowIfCancellationRequested(); var count = System.Math.Min(256, values.Length - offset);
            stream.ReadExactly(buffer[..(count * 49)]);
            for (var at = 0; at < count; at++) {
                var row = buffer.Slice(at * 49, 49);
                if (row[48] > 1) throw new InvalidDataException("Invalid staged lightmap coverage.");
                values[offset + at] = new(ReadVector(row), ReadVector(row[12..]), ReadVector(row[24..]), ReadVector(row[36..]), row[48] != 0);
            }
        }
    }

    private static void WriteVector(Span<byte> bytes, float3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value.x); BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], value.y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], value.z);
    }
    private static float3 ReadVector(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]), BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));

    // Borrows the FileStream; the create-site owns its disposal and exact-file receipt.
    private sealed class OutputStream(Stream file, ulong limit, CancellationToken token) : Stream
    {
        private ulong _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => checked((long)_written);
        public override long Position { get => Length; set => throw new NotSupportedException(); }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if ((uint)buffer.Length > limit - _written) throw new InvalidDataException("Lightmap output exceeds its admitted size.");
            file.Write(buffer); _written += (uint)buffer.Length;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void WriteByte(byte value) { ReadOnlySpan<byte> one = [value]; Write(one); }
        public override void Flush() { token.ThrowIfCancellationRequested(); file.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
#endif
