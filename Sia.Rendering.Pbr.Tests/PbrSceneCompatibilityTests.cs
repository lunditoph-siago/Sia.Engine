using System.IO.Compression;
using System.Security.Cryptography;
using Sia.Engine.Rendering.Pbr;
using Xunit;

public sealed class PbrSceneCompatibilityTests
{
    [Fact]
    public void OpticalParametersRoundTripWithoutChangingCoverage()
    {
        var material = new PbrMaterialAsset(PbrMaterial.Default, AlphaBlend: true, Opacity: .7f, Transmission: .9f, Thickness: .025f);
        var scene = PbrSceneAsset.Decode(PbrSceneAsset.Create([], [material], []).Encode());
        Assert.Equal(material, scene.Materials.Span[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OldDownloadedAssetsRetainAlphaAndHaveNoOpticalTransmission(int version)
    {
        // Independent legacy wire record, not a v3 encoder with a rewritten version.
        using var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, System.Text.Encoding.UTF8, true)) {
            w.Write("legacy"); w.Write(0); w.Write(0); w.Write(1);
            foreach (var value in new float[] { .8f, .8f, .8f, 0, .5f, 0, 0, 0, 0, 1, 1 }) { w.Write(value); }
            if (version == 2) { w.Write(true); w.Write(true); w.Write(.2f); }
            for (var i = 0; i < 5; i++) { w.Write(-1); }
            w.Write(0);
        }
        var raw = payload.ToArray();
        using var output = new MemoryStream();
        using (var w = new BinaryWriter(output, System.Text.Encoding.UTF8, true)) {
            w.Write("SIAPBR01"u8); w.Write(version); w.Write(raw.Length); w.Write(0L); w.Write(SHA256.HashData(raw));
        }
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)) { gzip.Write(raw); }
        var bytes = output.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), bytes.Length);
        var material = PbrSceneAsset.Decode(bytes).Materials.Span[0];
        Assert.Equal(version == 2 ? .2f : 1, material.Opacity);
        Assert.Equal(version == 2, material.AlphaBlend);
        Assert.Equal(0, material.Transmission); Assert.Equal(0, material.Thickness);
    }

    [Theory]
    [InlineData(-.1f, 0, true)]
    [InlineData(1.1f, 0, true)]
    [InlineData(float.NaN, 0, true)]
    [InlineData(1, -.01f, true)]
    [InlineData(1, float.PositiveInfinity, true)]
    [InlineData(1, 11, true)]
    [InlineData(1, .01f, false)]
    public void InvalidOpticalAssetsAreRejected(float transmission, float thickness, bool transparent)
    {
        var material = new PbrMaterialAsset(PbrMaterial.Default, AlphaBlend: transparent, Transmission: transmission, Thickness: thickness);
        Assert.Throws<ArgumentException>(() => PbrSceneAsset.Create([], [material], []));
    }
}
