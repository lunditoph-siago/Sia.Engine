using System.Security.Cryptography;
using Sia.Asset;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class ChecksummedAssetTests
{
    private readonly struct WordCodec : IChecksummedAssetCodec<int>
    {
        public static int HeaderBytes => 0;
        public static int GetReadSize(ReadOnlySpan<byte> header) => 4;
        public static int GetWriteSize(int declaredBytes) => declaredBytes;
        public static int ReadPayload(BinaryReader reader) => reader.ReadInt32();
        public static void WritePayload(BinaryWriter writer, int declaredBytes) => writer.Write(12345);
    }

    private readonly struct IncompleteReader : IChecksummedAssetCodec<int>
    {
        public static int HeaderBytes => 0;
        public static int GetReadSize(ReadOnlySpan<byte> header) => 4;
        public static int GetWriteSize(int value) => throw new NotSupportedException();
        public static int ReadPayload(BinaryReader reader) => reader.ReadInt16();
        public static void WritePayload(BinaryWriter writer, int value) => throw new NotSupportedException();
    }

    private readonly struct RejectDecode : IChecksummedAssetCodec<int>
    {
        public static int HeaderBytes => 0;
        public static int GetReadSize(ReadOnlySpan<byte> header) => 4;
        public static int GetWriteSize(int value) => throw new NotSupportedException();
        public static int ReadPayload(BinaryReader reader) => throw new InvalidOperationException("Decoder was invoked.");
        public static void WritePayload(BinaryWriter writer, int value) => throw new NotSupportedException();
    }

    [Fact]
    public void CallerBudgetIsCheckedBeforeReadingThePayload()
    {
        using var empty = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => ChecksummedAsset.Read<int, RejectDecode>(empty, 3));
        Assert.Equal(0, empty.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => ChecksummedAsset.Read<int, RejectDecode>(empty, -1));
    }

    [Fact]
    public void ExistingBufferReaderHonorsItsSliceAndValidatesBeforeDecoding()
    {
        using var encoded = new MemoryStream();
        ChecksummedAsset.Write<int, WordCodec>(encoded, 4);
        var bytes = encoded.ToArray();
        byte[] padded = [99, .. bytes, 88];
        var slice = padded.AsMemory(1, bytes.Length);
        Assert.Equal(12345, ChecksummedAsset.Read<int, WordCodec>(slice, 4));
        Assert.Throws<InvalidDataException>(() => ChecksummedAsset.Read<int, RejectDecode>(slice, 3));
        Assert.Throws<InvalidDataException>(() => ChecksummedAsset.Read<int, RejectDecode>(padded, 4));
        padded[1] ^= 1;
        Assert.Throws<InvalidDataException>(() => ChecksummedAsset.Read<int, RejectDecode>(slice, 4));
    }

    [Fact]
    public void InvalidWriterLengthsFailBeforeChangingTheDestination()
    {
        using var destination = new MemoryStream();
        destination.WriteByte(17);
        Assert.Throws<NotSupportedException>(() => ChecksummedAsset.Write<int, WordCodec>(destination, 3));
        Assert.Throws<InvalidDataException>(() => ChecksummedAsset.Write<int, WordCodec>(destination, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChecksummedAsset.Write<int, WordCodec>(destination, -1));
        Assert.Equal(new byte[] { 17 }, destination.ToArray());
        Assert.True(destination.CanWrite);
        Assert.Equal(1, destination.Position);
    }

    [Fact]
    public void ACodecMustConsumeTheCompleteVerifiedPayload()
    {
        using var encoded = new MemoryStream();
        ChecksummedAsset.Write<int, WordCodec>(encoded, 4);
        encoded.Position = 0;
        Assert.Throws<InvalidDataException>(() => ChecksummedAsset.Read<int, IncompleteReader>(encoded));
        Assert.True(encoded.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptionOrTrailingDataFailsBeforeTheDecoderRuns(bool trailing)
    {
        byte[] body = [1, 2, 3, 4];
        byte[] bytes = [.. body, .. SHA256.HashData(body)];
        if (trailing) bytes = [.. bytes, 0]; else bytes[0] ^= 1;
        using var source = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ChecksummedAsset.Read<int, RejectDecode>(source));
        Assert.True(source.CanRead);
    }
}
