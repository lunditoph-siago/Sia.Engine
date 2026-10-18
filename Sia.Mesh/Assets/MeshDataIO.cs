using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Mesh;

internal static class MeshDataIO
{
    internal ref struct Writer(Stream stream, IncrementalHash hash, Span<byte> buffer, CancellationToken token)
    {
        private readonly Stream _stream = stream;
        private readonly IncrementalHash _hash = hash;
        private readonly Span<byte> _buffer = buffer;
        private readonly CancellationToken _token = token;
        private int _used;

        public void Int(int value) => UInt(unchecked((uint)value));
        public void UInt(uint value)
        {
            if (_buffer.Length - _used < 4) Flush();
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer[_used..], value); _used += 4;
        }
        public void Float(float value) => Int(BitConverter.SingleToInt32Bits(value));
        public void Vector(float3 value) { Float(value.x); Float(value.y); Float(value.z); }
        public void Box(Aabb value) { Vector(value.Min); Vector(value.Max); }
        public void Vertex(MeshVertex value)
        {
            Vector(value.Position); Vector(value.Normal); Float(value.UV.x); Float(value.UV.y);
            Float(value.Tangent.x); Float(value.Tangent.y); Float(value.Tangent.z); Float(value.Tangent.w);
        }
        public void Bytes(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty) {
                if (_used == _buffer.Length) Flush();
                var count = System.Math.Min(bytes.Length, _buffer.Length - _used);
                bytes[..count].CopyTo(_buffer[_used..]); _used += count; bytes = bytes[count..];
            }
        }
        public void Flush()
        {
            _token.ThrowIfCancellationRequested();
            _hash.AppendData(_buffer[.._used]); _stream.Write(_buffer[.._used]); _used = 0;
        }
    }

    internal ref struct Reader(Stream stream, IncrementalHash hash, Span<byte> buffer, ulong length, CancellationToken token)
    {
        private readonly Stream _stream = stream;
        private readonly IncrementalHash _hash = hash;
        private readonly Span<byte> _buffer = buffer;
        private readonly CancellationToken _token = token;
        private ulong _remaining = length;
        private int _position;
        private int _count;

        public int Int() => unchecked((int)UInt());
        public uint UInt()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_position..]); _position += 4;
            return value;
        }
        public float Float() => BitConverter.Int32BitsToSingle(Int());
        public float3 Vector() => new(Float(), Float(), Float());
        public Aabb Box() => new(Vector(), Vector());
        public uint[] UIntArray(int count)
        {
            var result = new uint[count];
            for (var i = 0; i < result.Length; i++) result[i] = UInt();
            return result;
        }
        public void Bytes(Span<byte> bytes)
        {
            while (!bytes.IsEmpty) {
                Ensure(1);
                var count = System.Math.Min(bytes.Length, _count - _position);
                _buffer.Slice(_position, count).CopyTo(bytes); _position += count; bytes = bytes[count..];
            }
        }
        private void Ensure(int minimum)
        {
            if (_count - _position >= minimum) return;
            _token.ThrowIfCancellationRequested();
            var retained = _count - _position;
            _buffer.Slice(_position, retained).CopyTo(_buffer);
            var count = (int)System.Math.Min((ulong)(_buffer.Length - retained), _remaining);
            if (retained + count < minimum) throw new EndOfStreamException("Truncated virtual Mesh arrays.");
            _stream.ReadExactly(_buffer.Slice(retained, count));
            _hash.AppendData(_buffer.Slice(retained, count));
            _remaining -= (ulong)count; _position = 0; _count = retained + count;
        }
    }
}
