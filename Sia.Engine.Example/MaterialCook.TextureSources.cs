#if !BROWSER
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

internal static partial class MaterialCook
{
    private static PbrDiffuseMoments ComputeDiffuseSources(DiffuseSources sources, string directory, CancellationToken token)
    {
        ulong reserved = 0;
        using var color = OpenDiffuseSource(sources.BaseColor, directory, sources.MaximumBytes, ref reserved, token, out var colorHash);
        using var metal = OpenDiffuseSource(sources.MetallicRoughness, directory, sources.MaximumBytes, ref reserved, token, out var metalHash);
        var moments = PbrDiffuseMoments.Compute(color, metal, sources.MaximumBytes - reserved, token);
        if (colorHash is not null && moments.BaseColorHash != colorHash
            || metalHash is not null && moments.MetallicRoughnessHash != metalHash)
            throw new InvalidDataException("Diffuse moments differ from the indexed finest texture revision.");
        return moments;
    }

    private static Stream? OpenDiffuseSource(string? path, string directory, ulong maximumBytes,
        ref ulong reserved, CancellationToken token, out string? finestHash)
    {
        finestHash = null;
        if (path is null) return null;
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path, directory);
        var file = new DiffuseFile(path, token);
        try {
            Span<byte> header = stackalloc byte[64]; file.ReadExactly(header[..8]);
            if (header[..8].SequenceEqual("SIATEXR1"u8)) { file.Position = 0; return file; }
            var legacy = header[..8].SequenceEqual("SIATEXI1"u8);
            if (!legacy && !header[..8].SequenceEqual("SIATEXI2"u8))
                throw new InvalidDataException("Invalid diffuse texture source signature.");
            file.ReadExactly(header[8..]);
            var levels = (int)header[16]; var first = (int)header[17];
            var chunks = (int)BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
            var tailBytes = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
            var references = (ulong)first * 64 + (ulong)chunks * (legacy ? 36ul : 44ul);
            var rootBytes = (ulong)file.Length;
            if (rootBytes > maximumBytes - reserved)
                throw new InvalidDataException("Diffuse texture index exceeds admission.");
            if (levels is < 1 or > 14 || first >= levels || first > 13 || tailBytes < 64
                || tailBytes > rootBytes || references > rootBytes || 64 + references > rootBytes - tailBytes)
                throw new InvalidDataException("Invalid diffuse texture index framing.");
            if (64 + references + tailBytes != rootBytes)
                throw new InvalidDataException("Diffuse texture index length differs from its declaration.");
            var tailOwned = tailBytes - 64 + (ulong)(levels - first) * (ulong)Unsafe.SizeOf<ReadOnlyMemory<byte>>();
            var indexOwned = (ulong)first * ((ulong)Unsafe.SizeOf<PbrTextureMipReference>() + 128) + (ulong)chunks * 256;
            // The codec temporarily retains its reference bytes while allocating the tail and index.
            // TailMip0's embedded SIATEXR1 input is already charged by Compute; count its outer metadata only.
            ReserveDiffuseBytes(checked((first == 0 ? 64 + references : rootBytes) + tailOwned + indexOwned + references + 64),
                maximumBytes, ref reserved);
            using var replay = new DiffuseHeaderInput(file, header.ToArray(), token);
            var record = PbrTextureRecord.Load(replay, System.Math.Max(rootBytes, tailOwned + indexOwned));
            token.ThrowIfCancellationRequested();
            var index = record.Mips ?? throw new InvalidDataException("Diffuse source requires an indexed texture.");
            if (index.TailMip == 0) {
                ReserveDiffuseBytes((ulong)Unsafe.SizeOf<ReadOnlyMemory<byte>>() + 128, maximumBytes, ref reserved);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var output = new DiffuseHashOutput(hash, token);
                index.Tail.SliceMipChain(0, 1).Save(output);
                token.ThrowIfCancellationRequested();
                Span<byte> digest = stackalloc byte[32]; hash.GetHashAndReset(digest);
                finestHash = Convert.ToHexString(digest);
                // The authenticated tail ends at root EOF; Compute supports this nonzero origin.
                file.Position = checked(file.Length - (long)index.Tail.EncodedBytes);
                return file;
            }
            var finest = index.Mips[0];
            var cacheBytes = System.Math.Min(2L * AssetChunk.MaximumLength + 2, finest.Content.Length + finest.Content.Chunks.Length);
            var decodedBytes = 0;
            foreach (var chunk in finest.Content.Chunks)
                if (chunk.Encoding != AssetChunkEncoding.Raw) decodedBytes = System.Math.Max(decodedBytes, chunk.DecodedLength);
            ReserveDiffuseBytes((ulong)cacheBytes + (ulong)decodedBytes
                + (ulong)(finest.Content.Chunks.Length + 1) * sizeof(long) + (ulong)finest.Content.Chunks.Length * 256,
                maximumBytes, ref reserved);
            var input = new DiffuseChunkInput(finest.Content.Chunks, Path.GetDirectoryName(path)!, cacheBytes, decodedBytes, token);
            try {
                ValidateDiffuseFinest(input, index, finest);
                finestHash = finest.ContentHash;
                file.Dispose();
                return input;
            }
            catch { input.Dispose(); throw; }
        }
        catch { file.Dispose(); throw; }
    }

    private static void ValidateDiffuseFinest(Stream input, PbrTextureMipsData index, PbrTextureMipReference finest)
    {
        Span<byte> header = stackalloc byte[64]; input.ReadExactly(header);
        var sampler = index.Tail.Sampler;
        if (!header[..8].SequenceEqual("SIATEXR1"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != index.Width
            || BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) != index.Height
            || header[16] != (index.Tail.Srgb ? 1 : 0) || header[17] != 1
            || header[18] != (byte)sampler.AddressU || header[19] != (byte)sampler.AddressV
            || header[20] != (byte)sampler.MinFilter || header[21] != (byte)sampler.MagFilter
            || header[22] != (byte)sampler.MipFilter || header[23] != (sampler.UseMipmaps ? 1 : 0)
            || BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) != (ulong)finest.EncodedBytes - 64
            || input.Length != finest.EncodedBytes)
            throw new InvalidDataException("Diffuse finest texture differs from its index shape or sampling contract.");
        input.Position = 0;
    }

    private static void ReserveDiffuseBytes(ulong bytes, ulong maximumBytes, ref ulong reserved)
    {
        if (bytes > maximumBytes - reserved)
            throw new InvalidDataException("Diffuse texture sources and working storage exceed admission.");
        reserved += bytes;
    }

    private sealed class DiffuseHashOutput(IncrementalHash hash, CancellationToken token) : Stream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty) {
                token.ThrowIfCancellationRequested();
                var count = System.Math.Min(buffer.Length, 65536);
                hash.AppendData(buffer[..count]); buffer = buffer[count..];
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class DiffuseFile(string path, CancellationToken token) : Stream
    {
        private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            var count = _file.Read(buffer[..System.Math.Min(buffer.Length, 65536)]);
            token.ThrowIfCancellationRequested();
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int ReadByte() { Span<byte> value = stackalloc byte[1]; return Read(value) == 0 ? -1 : value[0]; }
        protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => _file.CanRead;
        public override bool CanSeek => _file.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _file.Length;
        public override long Position { get => _file.Position; set { token.ThrowIfCancellationRequested(); _file.Position = value; } }
        public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return _file.Seek(offset, origin); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // The admitted header is replayed so the typed codec cannot consume a different allocation declaration.
    private sealed class DiffuseHeaderInput(Stream source, byte[] header, CancellationToken token) : Stream
    {
        private int _offset;
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (_offset == header.Length) return source.Read(buffer);
            var count = System.Math.Min(buffer.Length, header.Length - _offset);
            header.AsSpan(_offset, count).CopyTo(buffer); _offset += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int ReadByte() { Span<byte> value = stackalloc byte[1]; return Read(value) == 0 ? -1 : value[0]; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Offline CLI only: synchronous row seeks reuse one current chunk and one admitted decode buffer.
    private sealed class DiffuseChunkInput : Stream
    {
        private readonly ImmutableArray<AssetChunk> _chunks;
        private readonly AssetChunkCache _cache;
        private readonly CancellationToken _token;
        private readonly long[] _starts;
        private byte[]? _decoded;
        private AssetChunkLease? _lease;
        private ReadOnlyMemory<byte> _bytes;
        private long _position;
        private int _index = -1;
        private bool _disposed;

        internal DiffuseChunkInput(ImmutableArray<AssetChunk> chunks, string directory, long cacheBytes, int decodedBytes, CancellationToken token)
        {
            _chunks = chunks; _token = token;
            _starts = new long[chunks.Length + 1];
            for (var i = 0; i < chunks.Length; i++) _starts[i + 1] = checked(_starts[i] + chunks[i].DecodedLength);
            _decoded = decodedBytes == 0 ? [] : GC.AllocateUninitializedArray<byte>(decodedBytes);
            _cache = new(AssetChunkSources.Directory(directory), cacheBytes, maximumConcurrentReads: 1, maximumPendingChunks: 1);
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _token.ThrowIfCancellationRequested();
            var read = 0;
            while (!buffer.IsEmpty && _position < _starts[^1]) {
                _token.ThrowIfCancellationRequested();
                var at = Array.BinarySearch(_starts, _position);
                if (at < 0) at = ~at - 1;
                if (_index != at) {
                    _lease?.Dispose(); _lease = null; _bytes = default; _index = -1;
                    var chunk = _chunks[at];
                    var lease = _cache.AcquireAsync(chunk, cancellationToken: _token).AsTask().GetAwaiter().GetResult();
                    try {
                        _token.ThrowIfCancellationRequested();
                        if (chunk.Encoding == AssetChunkEncoding.Raw) { _lease = lease; _bytes = lease.Memory; }
                        else {
                            chunk.Decode(lease.Memory.Span, _decoded!.AsSpan(0, chunk.DecodedLength));
                            _bytes = _decoded.AsMemory(0, chunk.DecodedLength); lease.Dispose();
                        }
                        _index = at;
                    }
                    catch { lease.Dispose(); throw; }
                }
                var offset = checked((int)(_position - _starts[_index]));
                var count = System.Math.Min(buffer.Length, _bytes.Length - offset);
                _bytes.Span.Slice(offset, count).CopyTo(buffer);
                buffer = buffer[count..]; _position += count; read += count;
            }
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed) {
                _disposed = true;
                _lease?.Dispose(); _lease = null; _bytes = default; _decoded = null;
                _cache.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int ReadByte() { Span<byte> value = stackalloc byte[1]; return Read(value) == 0 ? -1 : value[0]; }
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length { get { ObjectDisposedException.ThrowIf(_disposed, this); return _starts[^1]; } }
        public override long Position { get { ObjectDisposedException.ThrowIf(_disposed, this); return _position; } set => Seek(value, SeekOrigin.Begin); }
        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _token.ThrowIfCancellationRequested();
            var position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => _position,
                SeekOrigin.End => _starts[^1], _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
            if (position < 0 || position > _starts[^1]) throw new IOException("Seek exceeds the finest texture content.");
            return _position = position;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
#endif
