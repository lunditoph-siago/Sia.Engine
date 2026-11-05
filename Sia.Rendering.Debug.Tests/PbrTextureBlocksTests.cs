using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrTextureBlocksTests
{
    private static readonly string SourceHash = new('A', 64);
    private static readonly string BuildKey = new('B', 64);
    private static readonly float3 Average = new(.2f, .3f, .4f);

    [Theory]
    [InlineData(PbrTextureFormat.Bc7, true)]
    [InlineData(PbrTextureFormat.Bc7, false)]
    [InlineData(PbrTextureFormat.Bc5, false)]
    public async Task BlocksRoundTripThroughFragmentedBorrowedStreams(PbrTextureFormat format, bool srgb)
    {
        var original = Blocks(8, 4, format, srgb);
        var encoded = Encode(original);
        Assert.Equal("SIATEXB\0"u8.ToArray(), encoded[..8]);
        Assert.Equal(160 + 32 + 16 + 16 + 16, encoded.Length);
        using var sync = new FragmentedStream(encoded);
        var loaded = PbrTextureRecord.Load(sync, (ulong)encoded.Length);
        Assert.False(sync.Disposed);
        AssertTexture(original, loaded.Data);
        using var asyncInput = new FragmentedStream(encoded);
        var asynchronous = await PbrTextureRecord.LoadAsync(asyncInput, (ulong)encoded.Length);
        Assert.False(asyncInput.Disposed);
        AssertTexture(original, asynchronous.Data);
        Assert.Equal(encoded, Encode(asynchronous.Data));
    }

    [Fact]
    public void CanonicalRawSignatureAndLayoutRemainUnchanged()
    {
        var raw = PbrTextureData.Create(1, 1, true, [new byte[] { 128, 64, 32, 255 }]);
        var encoded = Encode(raw);
        Assert.Equal(PbrTextureFormat.Rgba8, raw.Format);
        Assert.Equal("SIATEXR\0"u8.ToArray(), encoded[..8]);
        Assert.Equal(68, encoded.Length);
        using var input = new MemoryStream(encoded);
        Assert.Equal((byte)128, PbrTextureData.Load(input).MipLevels.Span[0].Span[0]);
        Assert.InRange(raw.TransportAverage.x, .21585f, .21587f);
    }

    [Fact]
    public void BlocksSnapshotCallerMemoryAndSlicesPreserveCanonicalMetadata()
    {
        var first = Enumerable.Repeat((byte)73, 64).ToArray();
        var source = PbrTextureData.CreateBlocks(8, 8, true, PbrTextureFormat.Bc7,
            [first, new byte[16], new byte[16], new byte[16]], SourceHash, Average, BuildKey);
        first[0] = 0;
        Assert.Equal((byte)73, source.MipLevels.Span[0].Span[0]);
        var tail = source.SliceMipChain(1);
        Assert.Equal(SourceHash, tail.SourceLevel0Hash);
        Assert.Equal(BuildKey, tail.BuildKey);
        Assert.Equal(Average, tail.TransportAverage);
        Assert.Equal(PbrTextureFormat.Bc7, tail.Format);
        Assert.Equal(4u, tail.Width);
        Assert.Equal(3, tail.MipLevels.Length);
        using var input = new MemoryStream(Encode(tail));
        AssertTexture(tail, PbrTextureData.Load(input));
    }

    [Fact]
    public void IndexPreservesCanonicalRawIdentityRatherThanCompressedFileHash()
    {
        var blocks = Blocks(8, 8, PbrTextureFormat.Bc7, true);
        var fine = blocks.SliceMipChain(0, 1);
        var bytes = Encode(fine);
        var compressedHash = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.NotEqual(SourceHash, compressedHash);
        var reference = new PbrTextureMipReference(new(Guid.NewGuid()), compressedHash, bytes.Length);
        var index = PbrTextureMipsData.Create(8, 8, blocks.SliceMipChain(1), [reference]);
        using var output = new MemoryStream(); index.Save(output);
        Assert.Equal((byte)PbrTextureFormat.Bc7, output.GetBuffer()[20]);
        using var input = new MemoryStream(output.ToArray());
        var record = PbrTextureRecord.Load(input);
        Assert.NotNull(record.Mips);
        Assert.Equal(SourceHash, record.Data.SourceLevel0Hash);
        Assert.Equal(BuildKey, record.Data.BuildKey);
        using var fineInput = new MemoryStream(bytes);
        record.Mips.ValidateMip(0, PbrTextureRecord.Load(fineInput));
        var id = new AssetRefer<PbrTextureRecord>.Id(new(Guid.NewGuid()));
        var moments = new PbrDiffuseMoments(Average, Average, SourceHash, null);
        var material = new PbrMaterialData(PbrMaterial.Default, baseColor: id, diffuseMoments: moments);
        Assert.Same(moments, material.ResolveTextures(_ => record.Data).DiffuseMoments);
    }

    [Fact]
    public async Task AsyncIndexReaderKeepsStorageDeclarationAndBootstrapIdentity()
    {
        var blocks = Blocks(8, 8, PbrTextureFormat.Bc5, false);
        var fineBytes = Encode(blocks.SliceMipChain(0, 1));
        var reference = new PbrTextureMipReference(new(Guid.NewGuid()), Convert.ToHexString(SHA256.HashData(fineBytes)), fineBytes.Length);
        var index = PbrTextureMipsData.Create(8, 8, blocks.SliceMipChain(1), [reference]);
        using var output = new MemoryStream(); index.Save(output);
        using var fragmented = new FragmentedStream(output.ToArray());
        var loaded = await PbrTextureRecord.LoadAsync(fragmented, 4096);
        Assert.False(fragmented.Disposed);
        Assert.NotNull(loaded.Mips);
        Assert.Equal(PbrTextureFormat.Bc5, loaded.Mips.Format);
        Assert.Equal(SourceHash, loaded.Data.SourceLevel0Hash);
        using var fineInput = new MemoryStream(fineBytes);
        loaded.Mips.ValidateMip(0, PbrTextureRecord.Load(fineInput));
    }

    [Fact]
    public void IndexCannotMisdeclareAnotherKnownBlockFormatEvenWithRecomputedChecksum()
    {
        var blocks = Blocks(8, 8, PbrTextureFormat.Bc7, false);
        var fine = Encode(blocks.SliceMipChain(0, 1));
        var reference = new PbrTextureMipReference(new(Guid.NewGuid()), Convert.ToHexString(SHA256.HashData(fine)), fine.Length);
        using var output = new MemoryStream();
        PbrTextureMipsData.Create(8, 8, blocks.SliceMipChain(1), [reference]).Save(output);
        var bytes = output.ToArray(); bytes[20] = (byte)PbrTextureFormat.Bc5;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.AsSpan(0, 32)); hash.AppendData(bytes.AsSpan(64));
        hash.GetHashAndReset().CopyTo(bytes, 32);
        using var input = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => PbrTextureRecord.Load(input));
    }

    [Theory]
    [InlineData(68u, 68u, 4, false)] // 4 -> 8 -> 17 breaks block alignment.
    [InlineData(48u, 80u, 2, true)] // 12x20 -> 24x40 -> 48x80 remains aligned.
    public void SceneValidatesAllCompressedMipBasesBeforeReadingFineMips(uint width, uint height, int tailMip, bool supported)
    {
        var blocks = Blocks(width, height, PbrTextureFormat.Bc7, true);
        var references = new PbrTextureMipReference[tailMip];
        for (var mip = 0; mip < tailMip; mip++) {
            var bytes = Encode(blocks.SliceMipChain(mip, 1));
            references[mip] = new(new(Guid.NewGuid()), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        }
        var index = PbrTextureMipsData.Create(width, height, blocks.SliceMipChain(tailMip), references);
        var scene = PbrRasterScene.Create([], [new(PbrMaterial.Default, BaseColor: index.Tail)], []);
        PbrSceneStream Create() => PbrSceneStream.Create(scene, _ => index,
            static (_, _) => throw new InvalidOperationException("Scene creation cannot read fine mips."));
        if (supported) {
            Assert.Equal(tailMip, Assert.Single(Create().TextureSources.Values).TailMip);
        }
        else {
            var failure = Assert.Throws<InvalidDataException>(Create);
            Assert.Contains("block-aligned base mips", failure.Message);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IndexRejectsValidPayloadFromAnotherCanonicalSourceOrBuild(bool otherSource)
    {
        var expected = Blocks(8, 8, PbrTextureFormat.Bc7, true);
        var foreign = PbrTextureData.CreateBlocks(8, 8, true, PbrTextureFormat.Bc7,
            [expected.MipLevels.Span[0]], otherSource ? new string('C', 64) : SourceHash,
            Average, otherSource ? BuildKey : new string('D', 64));
        var bytes = Encode(foreign);
        var reference = new PbrTextureMipReference(new(Guid.NewGuid()), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        var index = PbrTextureMipsData.Create(8, 8, expected.SliceMipChain(1), [reference]);
        using var input = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => index.ValidateMip(0, PbrTextureRecord.Load(input)));
    }

    [Fact]
    public void InvalidBlocksAndRoleMismatchesCannotEnterMaterialUse()
    {
        Assert.Throws<ArgumentException>(() => PbrTextureData.CreateBlocks(4, 4, true, PbrTextureFormat.Bc5,
            [new byte[16]], SourceHash, Average, BuildKey));
        Assert.Throws<ArgumentException>(() => PbrTextureData.CreateBlocks(4, 4, false, PbrTextureFormat.Bc7,
            [new byte[15]], SourceHash, Average, BuildKey));
        Assert.Throws<ArgumentException>(() => PbrTextureData.CreateBlocks(4, 4, false, PbrTextureFormat.Bc7,
            [new byte[16]], SourceHash, new(float.NaN, 0, 0), BuildKey));
        var bc5 = Blocks(4, 4, PbrTextureFormat.Bc5, false);
        var reference = new AssetRefer<PbrTextureRecord>.Id(new(Guid.NewGuid()));
        var invalid = new PbrMaterialData(PbrMaterial.Default, metallicRoughness: reference);
        Assert.Throws<InvalidAssetException>(() => invalid.ResolveTextures(_ => bc5));
        var normal = new PbrMaterialData(PbrMaterial.Default, normal: reference);
        Assert.Same(bc5, normal.ResolveTextures(_ => bc5).Normal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(36)]
    [InlineData(48)]
    [InlineData(80)]
    [InlineData(128)]
    [InlineData(160)]
    public async Task TamperingIsRejectedBySyncAndAsyncReaders(int offset)
    {
        var bytes = Encode(Blocks(4, 4, PbrTextureFormat.Bc7, true));
        bytes[offset] ^= 0x40;
        using var input = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => PbrTextureRecord.Load(input));
        using var asyncInput = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await PbrTextureRecord.LoadAsync(asyncInput, 4096));
    }

    [Fact]
    public async Task ExactEofDecodeLimitsAndCancellationAreEnforced()
    {
        var bytes = Encode(Blocks(4, 4, PbrTextureFormat.Bc7, false));
        using var truncated = new MemoryStream(bytes[..^1]);
        Assert.Throws<InvalidDataException>(() => PbrTextureRecord.Load(truncated));
        using var trailing = new MemoryStream([.. bytes, 0]);
        Assert.Throws<InvalidDataException>(() => PbrTextureRecord.Load(trailing));
        using var overBudget = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => PbrTextureRecord.Load(overBudget, (ulong)bytes.Length - 1));
        var forged = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(8), uint.MaxValue);
        using var forgedInput = new MemoryStream(forged);
        Assert.Throws<InvalidDataException>(() => PbrTextureRecord.Load(forgedInput));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var cancelledInput = new MemoryStream(bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PbrTextureRecord.LoadAsync(cancelledInput, 4096, cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData(4096u, 22369648ul)]
    [InlineData(8192u, 89478512ul)]
    public void BlockFootprintIncludesOneBlockForTinyMips(uint size, ulong expected)
    {
        var layout = Blocks(4, 4, PbrTextureFormat.Bc7, false).BlockLayout;
        ulong bytes = 0;
        for (var dimension = size; ; dimension >>= 1) {
            bytes += layout.MipByteLength(dimension, dimension);
            if (dimension == 1) break;
        }
        Assert.Equal(expected, bytes);
        Assert.Equal(16ul, layout.MipByteLength(1, 1));
        Assert.Equal(64ul, layout.MipByteLength(5, 7));
    }

    private static PbrTextureData Blocks(uint width, uint height, PbrTextureFormat format, bool srgb)
    {
        var levels = new List<ReadOnlyMemory<byte>>();
        var w = width; var h = height;
        do {
            var length = checked((int)(((w + 3) / 4) * ((h + 3) / 4) * 16));
            levels.Add(Enumerable.Range(0, length).Select(at => (byte)(at * 17 + levels.Count)).ToArray());
            if (w == 1 && h == 1) break;
            w = System.Math.Max(1u, w / 2); h = System.Math.Max(1u, h / 2);
        } while (true);
        return PbrTextureData.CreateBlocks(width, height, srgb, format, levels.ToArray(), SourceHash, Average, BuildKey);
    }

    private static byte[] Encode(PbrTextureData data)
    {
        using var output = new MemoryStream(); data.Save(output); return output.ToArray();
    }

    private static void AssertTexture(PbrTextureData expected, PbrTextureData actual)
    {
        Assert.Equal(expected.Width, actual.Width); Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Format, actual.Format); Assert.Equal(expected.Srgb, actual.Srgb);
        Assert.Equal(expected.Sampler, actual.Sampler); Assert.Equal(expected.SourceLevel0Hash, actual.SourceLevel0Hash);
        Assert.Equal(expected.TransportAverage, actual.TransportAverage); Assert.Equal(expected.BuildKey, actual.BuildKey);
        Assert.Equal(expected.MipLevels.Length, actual.MipLevels.Length);
        for (var mip = 0; mip < expected.MipLevels.Length; mip++)
            Assert.Equal(expected.MipLevels.Span[mip].ToArray(), actual.MipLevels.Span[mip].ToArray());
    }

    private sealed class FragmentedStream(byte[] data) : Stream
    {
        private int _position;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(Span<byte> buffer) {
            var count = System.Math.Min(System.Math.Min(buffer.Length, 7), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
