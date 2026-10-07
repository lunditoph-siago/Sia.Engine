using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Engine.Rendering;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class LightmapStreamTests
{
    private static PbrLightmapAsset Field()
    {
        var coefficients = new float4[128 * 128 * 4];
        PbrLightmapReceiver[] receivers = [new(-1, 0, 0, 0, 64, default), new(-1, 3, 64, 0, 64, default)];
        foreach (var r in receivers) for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++) {
            if ((x + y) % 5 == 0) continue;
            var at = (y * 128 + r.X + x) * 4;
            coefficients[at] = new(default(float3), 1);
            if ((x + y) % 7 == 0) continue;
            var value = new float3(.1f + x * .01f, .2f + y * .01f, .03f + (x + y) * .005f) * (1 + r.StaticInstance * 20);
            coefficients[at] = new(value, 1); coefficients[at + 1] = new(value * -.7f, 0);
            coefficients[at + 2] = new(value * .3f, 0); coefficients[at + 3] = new(value * .5f, 0);
        }
        return new(128, receivers, new byte[32], new byte[32], new(), coefficients,
            [new(0, 0, 0, 64, 64), new(3, 64, 0, 64, 64)]);
    }

    private static async Task<(byte[] Metadata, Dictionary<string, byte[]> Chunks)> Cook(PbrLightmapAsset source)
    {
        var chunks = new Dictionary<string, byte[]>();
        var metadata = await PbrLightmapStream.CookAsync(source, (c, b, t) => {
            t.ThrowIfCancellationRequested(); chunks.Add(c.Id, b.ToArray()); return ValueTask.CompletedTask;
        });
        return (metadata, chunks);
    }

    [Fact]
    public async Task SparsePagesMatchResidentBaseMipsAndBordersWithoutEagerChunkReads()
    {
        var field = Field(); var compact = field.Quantize();
        var cooked = await Cook(field); var converted = await Cook(compact);
        Assert.Equal(cooked.Metadata, converted.Metadata);
        Assert.Equal(cooked.Chunks.Keys, converted.Chunks.Keys);
        await using var stream = await PbrLightmapStream.OpenAsync(cooked.Metadata,
            (c, t) => ValueTask.FromResult<Stream>(new MemoryStream(cooked.Chunks[c.Id], false)),
            byteBudget: cooked.Chunks.Values.Max(b => b.Length) + 1);
        Assert.Equal(0, stream.Statistics.ReadBytes);
        Assert.Equal(compact.SceneIdentity.ToArray(), stream.SceneIdentity.ToArray());
        Assert.Equal(compact.DecodeScales.ToArray(), stream.DecodeScales.ToArray());
        Assert.Equal(20, stream.Pages.Length);
        var levels = new byte[compact.MipCount][];
        for (var mip = 0; mip < compact.MipCount; mip++) {
            var side = compact.Resolution >> mip; levels[mip] = new byte[side * side * 16];
            var scratch = new byte[side * side * 4];
            for (var band = 0; band < 4; band++) {
                compact.WriteMipRows(scratch, mip, band, 0, side);
                for (var pixel = 0; pixel < side * side; pixel++) scratch.AsSpan(pixel * 4, 4).CopyTo(levels[mip].AsSpan(pixel * 16 + band * 4, 4));
            }
        }
        for (var i = 0; i < stream.Pages.Length; i++) {
            var p = stream.Pages.Span[i]; var r = stream.Receivers.Span[p.Receiver];
            var page = await stream.ReadPageAsync(i); Assert.Equal(PbrLightmapStream.PageBytes, page.Length);
            for (var y = 0; y < PbrLightmapStream.PageSide; y++) for (var x = 0; x < PbrLightmapStream.PageSide; x++) {
                var localX = p.X * 32 + x - 1; var localY = p.Y * 32 + y - 1;
                var actual = page.Slice((y * PbrLightmapStream.PageSide + x) * 16, 16).ToArray();
                if (localX < 0 || localY < 0 || localX >= (r.Resolution >> p.Mip) || localY >= (r.Resolution >> p.Mip))
                    Assert.All(actual, b => Assert.Equal(0, b));
                else {
                    var pixel = ((r.Y >> p.Mip) + localY) * (128 >> p.Mip) + (r.X >> p.Mip) + localX;
                    Assert.Equal(levels[p.Mip].AsSpan(pixel * 16, 16).ToArray(), actual);
                }
            }
        }
        Assert.True(stream.Statistics.ReadBytes > 0);
        Assert.True(stream.Statistics.Evictions > 0); // Every page fits; all pages together do not.
        Assert.Equal(levels[6].AsSpan(0, 16).ToArray(), stream.CoarseCoefficients.Span[..16].ToArray());
        Assert.Equal(levels[6].AsSpan(16, 16).ToArray(), stream.CoarseCoefficients.Span[16..].ToArray());
    }

    private static byte[] Rehash(byte[] original, Action<byte[]> change)
    {
        var bytes = original.ToArray(); change(bytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SIALMST1-L1UNORM8-CHARTMEAN-PAGE32-BORDER1"u8);
        hash.AppendData(bytes.AsSpan(0, 92)); hash.AppendData(bytes.AsSpan(124, bytes.Length - 32 - 124));
        hash.GetHashAndReset().CopyTo(bytes, 92);
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes, bytes.Length - 32);
        return bytes;
    }

    [Fact]
    public async Task MetadataAndChunkValidationRejectWrongAddressesScalesCoverageAndCorruption()
    {
        var cooked = await Cook(Field());
        var reads = 0;
        ValueTask<Stream> Open(AssetChunk c, CancellationToken t) { reads++; return ValueTask.FromResult<Stream>(new MemoryStream(cooked.Chunks[c.Id], false)); }
        async Task Reject(byte[] bytes) => await Assert.ThrowsAsync<InvalidDataException>(() => PbrLightmapStream.OpenAsync(bytes, Open));
        var receiver = 228; var chart = receiver + 2 * 80; var coarse = chart + 2 * 20; var page = coarse + 2 * 16;
        await Reject(Rehash(cooked.Metadata, b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), 63)));
        await Reject(Rehash(cooked.Metadata, b => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(receiver + 16), float.NaN)));
        await Reject(Rehash(cooked.Metadata, b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(chart), 99)));
        await Reject(Rehash(cooked.Metadata, b => b[coarse + 3] = 0));
        await Reject(Rehash(cooked.Metadata, b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(page + 8), 2)));
        await Reject(Rehash(cooked.Metadata, b => b.AsSpan(page, 20).CopyTo(b.AsSpan(page + 20, 20))));
        var badIdentity = cooked.Metadata.ToArray(); badIdentity[92] ^= 1;
        SHA256.HashData(badIdentity.AsSpan(0, badIdentity.Length - 32)).CopyTo(badIdentity, badIdentity.Length - 32);
        await Reject(badIdentity);
        await Assert.ThrowsAsync<InvalidDataException>(() => PbrLightmapStream.OpenAsync(cooked.Metadata, Open, 1));
        Assert.Equal(0, reads);
        await using var stream = await PbrLightmapStream.OpenAsync(cooked.Metadata, (c, t) => {
            var bytes = cooked.Chunks[c.Id].ToArray(); bytes[^1] ^= 1;
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, false));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => stream.ReadPageAsync(0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadPageAsync(0, new(true)));
        await stream.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.ReadPageAsync(0));
        foreach (var mutateHeader in new[] { true, false }) {
            var decoded = SceneStreamBlock.Decode(cooked.Chunks.First().Value);
            if (mutateHeader) BinaryPrimitives.WriteInt32LittleEndian(decoded.AsSpan(12), 2);
            else decoded[16 + 7] = 1;
            var encoded = SceneStreamBlock.Encode(decoded); var replacement = AssetChunk.FromBytes(encoded);
            var revised = Rehash(cooked.Metadata, b => {
                var manifestSize = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(24));
                var firstChunk = b.Length - 32 - manifestSize + 16;
                Convert.FromHexString(replacement.Id).CopyTo(b, firstChunk);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(firstChunk + 32), replacement.Length);
            });
            await using var badPage = await PbrLightmapStream.OpenAsync(revised, (c, t) =>
                ValueTask.FromResult<Stream>(new MemoryStream(c.Id == replacement.Id ? encoded : cooked.Chunks[c.Id], false)));
            await Assert.ThrowsAsync<InvalidDataException>(() => badPage.ReadPageAsync(0));
        }
    }

    private static PbrSceneAsset Scene() => PbrSceneAsset.Create([MeshPatchAsset.Cook(new([
        new(new(0, 0, 0), new(0, 0, 1), default), new(new(1, 0, 0), new(0, 0, 1), default),
        new(new(0, 1, 0), new(0, 0, 1), default)], [0, 1, 2], new(float3.zero, new(1, 1, 0))))],
        [new(new() { BaseColor = new(1) }, DoubleSided: true)], [new(0, 0, float4x4.identity),
        new(0, 0, new(new(1, 0, 0, 0), new(0, 1, 0, 0), new(0, 0, 1, 0), new(2, 0, 0, 1)))]);

    [Fact]
    public async Task DirectReceiverBakeMatchesResidentConversionAndKeepsSourceImmutable()
    {
        var scene = Scene(); var original = scene.Encode();
        var options = new PbrLightmapBakeSettings { Samples = 32 };
        var reference = PbrLightmapBaker.BakeAdaptive(scene, out var mapped, options, 16, 16, encoding: PbrLightmapEncoding.L1Unorm8);
        var expected = await Cook(reference); var chunks = new Dictionary<string, byte[]>();
        var baked = await PbrLightmapBaker.BakeStreamAsync(scene, (c, b, t) => { chunks.Add(c.Id, b.ToArray()); return ValueTask.CompletedTask; }, options, 16, 16);
        Assert.Equal(expected.Metadata, baked.Metadata); Assert.Equal(mapped.Encode(), baked.Scene.Encode());
        Assert.Equal(original, scene.Encode());
        Assert.Equal(expected.Chunks.Keys, chunks.Keys);
        foreach (var c in chunks) Assert.Equal(expected.Chunks[c.Key], c.Value);
    }

    [Fact]
    public async Task BudgetCancellationAndWriteFailureDoNotPublishAStreamOrMutateSource()
    {
        var writes = 0;
        ValueTask Write(AssetChunk c, ReadOnlyMemory<byte> b, CancellationToken t) { writes++; return ValueTask.CompletedTask; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => PbrLightmapStream.CookAsync(Field(), Write, 1));
        var scene = Scene(); var original = scene.Encode();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PbrLightmapBaker.BakeStreamAsync(scene, Write,
            minimumReceiverResolution: 16, maximumReceiverResolution: 16, maximumMetadataBytes: 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PbrLightmapBaker.BakeStreamAsync(scene, Write,
            minimumReceiverResolution: 16, maximumReceiverResolution: 16, maximumWorkingBytes: 16 * 16 * 132 - 1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PbrLightmapBaker.BakeStreamAsync(scene, Write, cancellationToken: new(true)));
        Assert.Equal(0, writes);
        await Assert.ThrowsAsync<IOException>(() => PbrLightmapBaker.BakeStreamAsync(scene,
            (c, b, t) => throw new IOException("Fixture destination unavailable."), new() { Samples = 16 }, 16, 16));
        Assert.Equal(original, scene.Encode());
    }
}
