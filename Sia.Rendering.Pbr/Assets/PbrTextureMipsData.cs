using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrTextureMipReference
{
    public AssetContent<PbrTextureRecord> Content { get; }
    public AssetId Id => Content?.Id ?? default;
    public string ContentHash { get; }
    public int EncodedBytes => (int)(Content?.DecodedLength ?? 0);
    public long StoredBytes => Content?.Length ?? 0;
    public AssetRefer<PbrTextureRecord>.Id Reference => new(Id);

    public PbrTextureMipReference(AssetId id, string contentHash, int encodedBytes)
        : this(new(id, [new AssetChunk(contentHash, encodedBytes)]), contentHash) { }

    public PbrTextureMipReference(AssetContent<PbrTextureRecord> content, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (contentHash is not { Length: 64 } || !contentHash.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Mip content requires a SHA-256 digest.", nameof(contentHash));
        if (content.DecodedLength is < 64 or > 268435520) throw new ArgumentOutOfRangeException(nameof(content));
        long remaining = content.DecodedLength;
        foreach (var chunk in content.Chunks) {
            if (chunk.DecodedLength > remaining || !chunk.Dependencies.IsEmpty)
                throw new InvalidDataException("Mip content requires ordered bounded chunks without external dependencies.");
            remaining -= chunk.DecodedLength;
        }
        if (remaining != 0) throw new InvalidDataException("Incomplete texture mip chunks.");
        Content = content; ContentHash = contentHash.ToUpperInvariant();
    }
}

/// <summary>Complete texture shape, independently readable fine mips and an immutable bootstrap tail. No IO or GPU owner.</summary>
public sealed class PbrTextureMipsData
{
    private const int HeaderBytes = 64, ReferenceBytes = 64, ChunkBytes = 44;
    private readonly PbrTextureMipReference[] _mips;
    public uint Width { get; }
    public uint Height { get; }
    public int Levels => TailMip + Tail.MipLevels.Length;
    public int TailMip => _mips.Length;
    public PbrTextureData Tail { get; }
    public ReadOnlySpan<PbrTextureMipReference> Mips => _mips;
    public ulong EncodedBytes => HeaderBytes + (ulong)TailMip * ReferenceBytes
        + (ulong)_mips.Sum(m => m.Content.Chunks.Length) * ChunkBytes + Tail.EncodedBytes;

    private PbrTextureMipsData(uint width, uint height, PbrTextureData tail, PbrTextureMipReference[] mips)
        => (Width, Height, Tail, _mips) = (width, height,
            mips.Length == 0 ? tail : tail.WithSourceLevel0Hash(mips[0].ContentHash), mips);

    public static PbrTextureMipsData Create(uint width, uint height, PbrTextureData tail,
        ReadOnlySpan<PbrTextureMipReference> mips)
    {
        ArgumentNullException.ThrowIfNull(tail);
        if (mips.Length > 13 || !PbrTextureData.ValidShape(width, height, mips.Length + tail.MipLevels.Length, tail.Sampler)
            || tail.Width != System.Math.Max(1u, width >> mips.Length) || tail.Height != System.Math.Max(1u, height >> mips.Length))
            throw new InvalidDataException("Mip index and bootstrap texture shapes disagree.");
        var ids = new HashSet<AssetId>();
        var chunkCount = 0;
        for (var mip = 0; mip < mips.Length; mip++) {
            var reference = mips[mip];
            var bytes = 64L + (long)System.Math.Max(1u, width >> mip) * System.Math.Max(1u, height >> mip) * 4;
            if (reference.Content is null || reference.Id.Value == Guid.Empty || !ids.Add(reference.Id) || reference.EncodedBytes != bytes
                || !ValidHash(reference.ContentHash)) throw new InvalidDataException("Invalid or duplicate texture mip reference.");
            chunkCount = checked(chunkCount + reference.Content.Chunks.Length);
            if (chunkCount > ushort.MaxValue) throw new InvalidDataException("Texture mip index has too many independent blocks.");
        }
        return new(width, height, tail, mips.ToArray());
    }

    /// <summary>Validates the selected standalone mip before it can contribute to a streamed tail.</summary>
    public void ValidateMip(int mip, PbrTextureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if ((uint)mip >= (uint)TailMip) throw new ArgumentOutOfRangeException(nameof(mip));
        var data = record.Data;
        if (record.Mips is not null || data.Width != System.Math.Max(1u, Width >> mip)
            || data.Height != System.Math.Max(1u, Height >> mip) || data.MipLevels.Length != 1
            || data.Srgb != Tail.Srgb || data.Sampler != Tail.Sampler || data.EncodedBytes != (ulong)_mips[mip].EncodedBytes)
            throw new InvalidDataException("Texture mip does not match its index shape or sampling contract.");
        if (data.EncodedHash != _mips[mip].ContentHash)
            throw new InvalidDataException("Texture mip content differs from its index.");
    }

    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[HeaderBytes]; header.Clear();
        "SIATEXI\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Height);
        header[16] = (byte)Levels; header[17] = (byte)TailMip;
        var chunkCount = _mips.Sum(m => m.Content.Chunks.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[18..], (ushort)chunkCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], Tail.EncodedBytes);
        var references = new byte[TailMip * ReferenceBytes + chunkCount * ChunkBytes];
        var firstChunk = 0;
        for (var mip = 0; mip < TailMip; mip++) {
            var row = references.AsSpan(mip * ReferenceBytes, ReferenceBytes);
            _mips[mip].Id.Value.TryWriteBytes(row);
            Convert.FromHexString(_mips[mip].ContentHash).CopyTo(row[16..]);
            BinaryPrimitives.WriteInt32LittleEndian(row[48..], firstChunk);
            BinaryPrimitives.WriteInt32LittleEndian(row[52..], _mips[mip].Content.Chunks.Length);
            BinaryPrimitives.WriteInt64LittleEndian(row[56..], _mips[mip].EncodedBytes);
            foreach (var chunk in _mips[mip].Content.Chunks) {
                var target = references.AsSpan(TailMip * ReferenceBytes + firstChunk++ * ChunkBytes, ChunkBytes);
                Convert.FromHexString(chunk.Id).CopyTo(target); BinaryPrimitives.WriteInt32LittleEndian(target[32..], chunk.Length);
                BinaryPrimitives.WriteInt32LittleEndian(target[36..], chunk.DecodedLength);
                BinaryPrimitives.WriteInt32LittleEndian(target[40..], (int)chunk.Encoding);
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header[..32]); hash.AppendData(references);
        Tail.Save(new DigestStream(Stream.Null, hash));
        hash.GetHashAndReset().CopyTo(header[32..]);
        stream.Write(header); stream.Write(references); Tail.Save(stream);
    }

    public static PbrTextureMipsData Load(Stream stream, ulong maximumBytes = 128ul * 1024 * 1024)
        => Load(stream, maximumBytes, default);

    internal static PbrTextureMipsData Load(Stream stream, ulong maximumBytes, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes < HeaderBytes) throw new InvalidDataException("Mip index header exceeds its decode limit.");
        try {
            Span<byte> header = stackalloc byte[HeaderBytes];
            if (signature.IsEmpty) stream.ReadExactly(header);
            else {
                if (signature.Length != 8) throw new ArgumentException("Texture signature must contain eight bytes.", nameof(signature));
                signature.CopyTo(header); stream.ReadExactly(header[8..]);
            }
            var width = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            var height = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            var levels = (int)header[16]; var first = (int)header[17];
            var chunkCount = (int)BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
            var tailBytes = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
            _ = ValidateHeader(header, maximumBytes);
            var expectedTail = tailBytes;
            var tailOwned = expectedTail - 64 + (ulong)(levels - first) * (ulong)Unsafe.SizeOf<ReadOnlyMemory<byte>>();
            var references = new byte[first * ReferenceBytes + chunkCount * ChunkBytes]; stream.ReadExactly(references);
            var chunks = new AssetChunk[chunkCount];
            var chunkLengths = new Dictionary<string, (int Stored, int Decoded, AssetChunkEncoding Encoding)>(StringComparer.Ordinal);
            for (var c = 0; c < chunkCount; c++) {
                var row = references.AsSpan(first * ReferenceBytes + c * ChunkBytes, ChunkBytes);
                var length = BinaryPrimitives.ReadInt32LittleEndian(row[32..]);
                var decoded = BinaryPrimitives.ReadInt32LittleEndian(row[36..]);
                var encoding = BinaryPrimitives.ReadInt32LittleEndian(row[40..]);
                if (length is < 1 or > AssetChunk.MaximumLength || decoded is < 1 or > AssetChunk.MaximumLength
                    || encoding is < 0 or > (int)AssetChunkEncoding.Lz4
                    || (encoding == 0 ? decoded != length : length >= decoded))
                    throw new InvalidDataException("Invalid mip chunk encoding or length.");
                var digest = Convert.ToHexString(row[..32]);
                var shape = (length, decoded, (AssetChunkEncoding)encoding);
                if (chunkLengths.TryGetValue(digest, out var priorLength) && priorLength != shape)
                    throw new InvalidDataException("Conflicting mip chunk lengths for one digest.");
                chunkLengths[digest] = shape;
                chunks[c] = new(digest, length, decodedLength: decoded, encoding: (AssetChunkEncoding)encoding);
            }
            var mips = new PbrTextureMipReference[first];
            var ids = new HashSet<AssetId>();
            var nextChunk = 0;
            for (var mip = 0; mip < first; mip++) {
                var row = references.AsSpan(mip * ReferenceBytes, ReferenceBytes);
                var bytes = BinaryPrimitives.ReadInt64LittleEndian(row[56..]);
                var id = new AssetId(new Guid(row[..16]));
                var expected = 64L + (long)System.Math.Max(1u, width >> mip) * System.Math.Max(1u, height >> mip) * 4;
                var start = BinaryPrimitives.ReadInt32LittleEndian(row[48..]);
                var count = BinaryPrimitives.ReadInt32LittleEndian(row[52..]);
                if (id.Value == Guid.Empty || !ids.Add(id) || bytes != expected)
                    throw new InvalidDataException("Invalid or duplicate mip payload reference.");
                if (start != nextChunk || count < 1 || count > chunkCount - nextChunk) throw new InvalidDataException("Invalid mip chunk range.");
                var remaining = expected;
                for (var c = start; c < start + count; c++) {
                    if (chunks[c].DecodedLength > remaining)
                        throw new InvalidDataException("Mip chunks disagree with their payload length.");
                    remaining -= chunks[c].DecodedLength;
                }
                if (remaining != 0) throw new InvalidDataException("Incomplete texture mip payload.");
                mips[mip] = new(new(id, chunks.AsSpan(start, count).ToArray()), Convert.ToHexString(row[16..48]));
                nextChunk += count;
            }
            if (nextChunk != chunkCount) throw new InvalidDataException("Unreferenced mip content chunks.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(header[..32]); hash.AppendData(references);
            var payload = new DigestStream(stream, hash, (long)tailBytes);
            var tail = PbrTextureData.Load(payload, System.Math.Max(tailBytes, tailOwned));
            if (payload.Remaining != 0 || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), header[32..]))
                throw new InvalidDataException("Texture mip index checksum mismatch.");
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing texture mip index data.");
            return Create(width, height, tail, mips);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated texture mip index.", error); }
    }

    private static bool ValidHash(string? value)
        => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    internal static ulong ValidateHeader(ReadOnlySpan<byte> header, ulong maximumBytes)
    {
        var width = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var levels = (int)header[16]; var first = (int)header[17];
        var chunks = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        if (!header[..8].SequenceEqual("SIATEXI\0"u8) || header[20..24].IndexOfAnyExcept((byte)0) >= 0
            || width is 0 or > 8192 || height is 0 or > 8192 || levels is < 1 or > 14 || first >= levels
            || chunks < first
            || (levels > 1 && (width >> (levels - 2)) <= 1 && (height >> (levels - 2)) <= 1))
            throw new InvalidDataException("Invalid texture mip index header.");
        ulong tail = 64;
        for (var mip = first; mip < levels; mip++)
            tail += (ulong)System.Math.Max(1u, width >> mip) * System.Math.Max(1u, height >> mip) * 4;
        var metadata = HeaderBytes + (ulong)first * ReferenceBytes + (ulong)chunks * ChunkBytes;
        var tailOwned = tail - 64 + (ulong)(levels - first) * (ulong)Unsafe.SizeOf<ReadOnlyMemory<byte>>();
        var indexOwned = (ulong)first * ((ulong)Unsafe.SizeOf<PbrTextureMipReference>() + 128) + (ulong)chunks * 256;
        if (tail != BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) || metadata > maximumBytes || tail > maximumBytes - metadata
            || tailOwned > maximumBytes || indexOwned > maximumBytes - tailOwned)
            throw new InvalidDataException("Texture mip index exceeds its shape or decode limit.");
        return metadata + tail;
    }

    private sealed class DigestStream(Stream source, IncrementalHash hash, long remaining = long.MaxValue) : Stream
    {
        public long Remaining { get; private set; } = remaining;
        public override bool CanRead => source.CanRead;
        public override bool CanWrite => source.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(Span<byte> buffer)
        {
            if (Remaining == 0) return 0;
            var count = source.Read(buffer[..(int)System.Math.Min(buffer.Length, Remaining)]);
            hash.AppendData(buffer[..count]); Remaining -= count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int ReadByte() { Span<byte> value = stackalloc byte[1]; return Read(value) == 0 ? -1 : value[0]; }
        public override void Write(ReadOnlySpan<byte> buffer) { hash.AppendData(buffer); source.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() => source.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
