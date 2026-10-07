using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Engine.Rendering.Pbr;
using Sia.Engine.Mesh;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class LightmapMipTests
{
    [Fact]
    public void ResidentTrianglesRequireOneChartAndRejectUnmappedVertices()
    {
        var mesh = MeshPatchAsset.Cook(new([
            new(new(0, 0, 0), new(0, 0, 1), default) { LightmapUV = new(0, 0) },
            new(new(1, 0, 0), new(0, 0, 1), default) { LightmapUV = new(1, 0) },
            new(new(0, 1, 0), new(0, 0, 1), default) { LightmapUV = new(0, 1) }],
            [0, 1, 2], new(float3.zero, new(1, 1, 0))));
        using var geometry = new PbrResidentGeometry(mesh.Build.Tree);
        var map = new int[64];
        geometry.ValidateLightmapCharts(map, 8);
        map[7] = 1;
        Assert.Throws<ArgumentException>(() => geometry.ValidateLightmapCharts(map, 8));
        map[7] = -1;
        Assert.Throws<ArgumentException>(() => geometry.ValidateLightmapCharts(map, 8));
    }

    private static PbrLightmapAsset Fixture()
    {
        var values = new float4[16 * 16 * 4];
        void Set(int x, int y, float value) {
            var at = (y * 16 + x) * 4;
            values[at] = new(value, value * .5f, value * .25f, 1);
            values[at + 1] = new(-value, value * .25f, value * .5f, 0);
        }
        Set(0, 0, 4);
        for (var y = 0; y < 2; y++) for (var x = 2; x < 4; x++) Set(x, y, 0);
        for (var y = 0; y < 8; y++) for (var x = 8; x < 16; x++) Set(x, y, 64);
        return new(16, [new(-1, 0, 0, 0, 8, default), new(-1, 1, 8, 0, 8, default)],
            new byte[32], new byte[32], new(), values, [new(0, 0, 0, 8, 8), new(1, 8, 0, 8, 8)]);
    }

    [Fact]
    public void MipsAverageCoveredBaseSamplesPreserveBlackAndKeepAlignedChartsSeparate()
    {
        var asset = Fixture();
        Assert.Equal(4, asset.MipCount);
        Assert.Equal((256ul + 64 + 16 + 4) * 32, asset.TextureBytes);
        var row = new byte[16 * 8];
        float Read(int x, int channel) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(x * 8 + channel * 2)));
        asset.WriteMipRows(row, 1, 0, 0, 1);
        Assert.Equal(4, Read(0, 0)); // One covered texel is not divided by four.
        Assert.Equal(0, Read(1, 0)); Assert.Equal(1, Read(1, 3)); // Covered black.
        Assert.Equal(0, Read(2, 3)); // A true hole.
        asset.WriteMipRows(row, 2, 0, 0, 1);
        Assert.InRange(Read(0, 0), .799f, .801f); // Four black + one bright, not recursive equal weights.
        asset.WriteMipRows(row, 3, 1, 0, 1);
        Assert.InRange(Read(0, 0), -.801f, -.799f);
        Assert.Equal(-64, Read(1, 0)); // Neighbor did not enter the safe chart footprint.
        Assert.Equal(0, Read(0, 3)); // Directional bands never carry coverage.
        Assert.Equal(0, asset.ChartMaximumMip(new(0, 1, 0, 7, 8)));
        Assert.Throws<ArgumentOutOfRangeException>(() => asset.WriteMipRows(row, 4, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => asset.WriteMipRows(new byte[1], 1, 0, 0, 1));
        var compact = asset.Quantize();
        Assert.Equal(asset.TextureBytes / 2, compact.TextureBytes);
        var encoded = new byte[32];
        compact.WriteMipRows(encoded, 3, 0, 0, 1);
        Assert.Equal(51, encoded[0]); Assert.Equal(255, encoded[3]);
        compact.WriteMipRows(encoded, 1, 1, 0, 1);
        Assert.Equal(1, encoded[0]); Assert.Equal(128, encoded[4]); Assert.Equal(0, encoded[8]);
    }

    [Fact]
    public void ChartCodecVersionsIdentityAndRejectsMalformedOwnershipBoundsAndOverlap()
    {
        foreach (var asset in new[] { Fixture(), Fixture().Quantize() }) {
            var bytes = asset.Encode();
            Assert.Equal("SIALMAP3"u8.ToArray(), bytes[..8]);
            Assert.Equal(bytes, PbrLightmapAsset.Decode(bytes).Encode());
            Assert.Equal(asset.Charts.ToArray(), PbrLightmapAsset.Decode(bytes).Charts.ToArray());
            var header = 224 + asset.Receivers.Length * (asset.Encoding == PbrLightmapEncoding.L1Half ? 16 : 80);
            byte[] Change(int offset, int value) {
                var changed = bytes.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(offset), value);
                SHA256.HashData(changed.AsSpan(0, changed.Length - 32)).CopyTo(changed.AsSpan(changed.Length - 32));
                return changed;
            }
            Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Change(8, 2)));
            Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Change(12, 0)));
            Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Change(header, 3)));
            Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Change(header + 4, -1)));
            Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Change(header + 12, 9)));
            Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(Change(header + 20, 0)));
            Assert.Throws<InvalidDataException>(() => PbrLightmapAsset.Decode(bytes, bytes.Length - 1));
        }
        var fixture = Fixture();
        var legacy = PbrLightmapAsset.FromHalf(16, fixture.Receivers.Span, fixture.SceneIdentity.Span,
            fixture.SurfaceIdentity.Span, fixture.Settings, fixture.Data.ToArray());
        Assert.Equal(1, legacy.MipCount);
        Assert.Equal("SIALMAP1"u8.ToArray(), legacy.Encode()[..8]);
        Assert.False(legacy.BakeIdentity.Span.SequenceEqual(fixture.BakeIdentity.Span));
    }
}
