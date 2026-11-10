using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sia.Engine.Rendering;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class DisplayTransformDataTests
{
    private const string Recipe = "C0FFEE0123456789C0FFEE0123456789C0FFEE0123456789C0FFEE0123456789";

    private static float[] Cube()
    {
        var result = new float[2 * 2 * 2 * 3];
        for (var b = 0; b < 2; b++)
            for (var g = 0; g < 2; g++)
                for (var r = 0; r < 2; r++) {
                    var at = ((b * 2 + g) * 2 + r) * 3;
                    result[at] = r; result[at + 1] = g; result[at + 2] = b;
                }
        return result;
    }

    private static DisplayTransformStage Identity() => DisplayTransformStage.Matrix(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), default);
    private static DisplayTransformData Data(params DisplayTransformStage[] stages) => DisplayTransformData.Create("Linear test", "Display test", Recipe, stages);
    private static byte[] Encoded(DisplayTransformData data)
    { using var stream = new MemoryStream(); data.Write(stream); return stream.ToArray(); }
    private static DisplayTransformData Decode(byte[] bytes) => DisplayTransformData.Read(new MemoryStream(bytes, writable: false));

    [Fact]
    public void InputArraysListsAndLookupOrderHaveIndependentImmutableOwnership()
    {
        var rgb = Cube(); var stage = DisplayTransformStage.Lut3D(2, rgb);
        var stages = new List<DisplayTransformStage> { Identity(), stage };
        var data = DisplayTransformData.Create("Linear test", "Display test", Recipe.ToLowerInvariant(), stages);
        var bytes = Encoded(data);
        Array.Clear(rgb); stages.Clear();
        Assert.Equal(bytes, Encoded(data));
        Assert.Equal(Recipe, data.SourceRecipeHash);
        Assert.Equal(2, data.Stages.Count);
        var table = Assert.Single(data.LookupTables);
        Assert.Equal(2, table.Size); Assert.Equal(Cube(), table.Rgb32F.ToArray());
        Assert.Equal(96, table.OwnedBytes); Assert.Equal(96, data.OwnedBytes);
        Assert.Throws<NotSupportedException>(() => ((IList<DisplayTransformStage>)data.Stages).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DisplayTransformLut3D>)data.LookupTables).Clear());
    }

    [Fact]
    public void EveryTypedStageRoundTripsWithStableContentIdentityAndSourceProvenance()
    {
        var data = Data(Identity(), DisplayTransformStage.Log(2), DisplayTransformStage.Log(10, inverse: true),
            DisplayTransformStage.Exponent(new(2.4f), DisplayTransformNegativeStyle.Clamp),
            DisplayTransformStage.Exponent(new(2), DisplayTransformNegativeStyle.Mirror, inverse: true),
            DisplayTransformStage.Exponent(new(1.5f), DisplayTransformNegativeStyle.PassThrough),
            DisplayTransformStage.MonCurve(new(2.4f), new(.055f)),
            DisplayTransformStage.MonCurve(new(2.4f), new(.055f), inverse: true, negativeStyle: DisplayTransformNegativeStyle.PassThrough),
            DisplayTransformStage.Lut3D(2, Cube()));
        using var output = new MemoryStream(); var hash = data.Write(output); var bytes = output.ToArray();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), hash);
        var restored = Decode(bytes);
        Assert.Equal(data.InputColorSpace, restored.InputColorSpace); Assert.Equal(data.OutputColorSpace, restored.OutputColorSpace);
        Assert.Equal(data.SourceRecipeHash, restored.SourceRecipeHash); Assert.Equal(data.Stages.Count, restored.Stages.Count);
        Assert.Equal(bytes, Encoded(restored));
        Assert.Equal(DisplayTransformShaderSource.Compile(data, "test/display"), DisplayTransformShaderSource.Compile(restored, "test/display"));
    }

    [Fact]
    public void EveryOperationRoundTripsAtItsExactEncodedByteLimit()
    {
        DisplayTransformStage[] stages = [Identity(), DisplayTransformStage.Log(2),
            DisplayTransformStage.Exponent(new(2.4f)), DisplayTransformStage.MonCurve(new(2.4f), new(.055f)),
            DisplayTransformStage.Lut3D(2, Cube())];
        foreach (var program in stages.Select(stage => new[] { stage }).Append(stages)) {
            var data = Data(program); var bytes = Encoded(data); var exact = (ulong)bytes.Length;
            var bounded = DisplayTransformData.Create(data.InputColorSpace, data.OutputColorSpace,
                data.SourceRecipeHash, data.Stages, maximumBytes: exact);
            Assert.Equal(bytes, Encoded(bounded));
            Assert.Equal(bytes, Encoded(DisplayTransformData.Read(new MemoryStream(bytes), maximumBytes: exact)));
            Assert.Throws<ArgumentException>(() => DisplayTransformData.Create(data.InputColorSpace, data.OutputColorSpace,
                data.SourceRecipeHash, data.Stages, maximumBytes: exact - 1));
            Assert.Throws<InvalidDataException>(() => DisplayTransformData.Read(new MemoryStream(bytes), maximumBytes: exact - 1));
        }
    }

    [Fact]
    public void RepeatedLutStageChargesEveryEncodedOccurrenceWithoutDeduplication()
    {
        var stage = DisplayTransformStage.Lut3D(2, Cube());
        var one = Encoded(Data(stage)); var two = Encoded(Data(stage, stage));
        // One new flags4/size4/RGB payload is written for the repeated occurrence.
        Assert.Equal(one.Length + 8 + 2 * 2 * 2 * 3 * sizeof(float), two.Length);
        var exact = (ulong)two.Length;
        var bounded = DisplayTransformData.Create("Linear test", "Display test", Recipe, [stage, stage], maximumBytes: exact);
        Assert.Equal(2, bounded.LookupTables.Count);
        Assert.Equal(two, Encoded(bounded));
        Assert.Equal(two, Encoded(DisplayTransformData.Read(new MemoryStream(two), maximumBytes: exact)));
        Assert.Throws<ArgumentException>(() => DisplayTransformData.Create("Linear test", "Display test", Recipe,
            [stage, stage], maximumBytes: exact - 1));
        Assert.Throws<InvalidDataException>(() => DisplayTransformData.Read(new MemoryStream(two), maximumBytes: exact - 1));
        Assert.Throws<ArgumentException>(() => Data(Enumerable.Repeat(stage, DisplayTransformData.MaximumLookupTables + 1).ToArray()));
    }

    [Fact]
    public void RejectsNonfiniteInvalidAndUnboundedAuthoredInputs()
    {
        Assert.Throws<ArgumentException>(() => DisplayTransformStage.Matrix(new(float.NaN, 0, 0), default, default, default));
        foreach (var value in new[] { 0f, 1f, -1f, float.NaN, float.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => DisplayTransformStage.Log(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayTransformStage.Exponent(new(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayTransformStage.Exponent(new(2), DisplayTransformNegativeStyle.Linear));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayTransformStage.MonCurve(new(1), new(.055f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayTransformStage.MonCurve(new(2.4f), new(0)));
        Assert.Throws<ArgumentException>(() => DisplayTransformStage.Lut3D(130, []));
        Assert.Throws<ArgumentException>(() => DisplayTransformStage.Lut3D(2, new float[23]));
        var nonfinite = Cube(); nonfinite[2] = float.PositiveInfinity;
        Assert.Throws<ArgumentException>(() => DisplayTransformStage.Lut3D(2, nonfinite));
        Assert.Throws<ArgumentException>(() => DisplayTransformData.Create("a", "b", "not-a-hash", [Identity()]));
        Assert.Throws<ArgumentException>(() => DisplayTransformData.Create("a\nb", "b", Recipe, [Identity()]));
        Assert.Throws<ArgumentException>(() => Data([]));
        Assert.Throws<ArgumentException>(() => Data(Enumerable.Repeat(Identity(), DisplayTransformData.MaximumStages + 1).ToArray()));
        Assert.Throws<ArgumentException>(() => Data(Enumerable.Repeat(DisplayTransformStage.Lut3D(2, Cube()), DisplayTransformData.MaximumLookupTables + 1).ToArray()));
        // This exact encoded program is194 bytes; the cap must reject193, not200.
        Assert.Throws<ArgumentException>(() => DisplayTransformData.Create("a", "b", Recipe, [DisplayTransformStage.Lut3D(2, Cube())], maximumBytes: 193));
    }

    [Fact]
    public void BinaryReaderRejectsDamageTruncationTrailingBytesAndOversizedAdmission()
    {
        var bytes = Encoded(Data(Identity()));
        var corrupt = bytes.ToArray(); corrupt[corrupt.Length / 2] ^= 1;
        Assert.Throws<InvalidDataException>(() => Decode(corrupt));
        Assert.Throws<InvalidDataException>(() => Decode(bytes[..^1]));
        Assert.Throws<InvalidDataException>(() => Decode([.. bytes, 0]));
        Assert.Throws<InvalidDataException>(() => DisplayTransformData.Read(new MemoryStream(bytes), maximumBytes: 48));
        var oversized = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(12), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => Decode(oversized));
    }

    [Theory]
    [InlineData("unknown-stage")]
    [InlineData("reserved-flags")]
    [InlineData("matrix-inverse")]
    [InlineData("nan-matrix")]
    public void BinaryReaderValidatesSemanticsEvenWithAValidChecksum(string mutation)
    {
        var bytes = Encoded(Data(Identity()));
        var stage = 16 + 2 + Encoding.UTF8.GetByteCount("Linear test") + 2 + Encoding.UTF8.GetByteCount("Display test") + 32 + 4;
        switch (mutation) {
            case "unknown-stage": bytes[stage] = 255; break;
            case "reserved-flags": bytes[stage + 3] = 1; break;
            case "matrix-inverse": bytes[stage + 1] = 1; break;
            case "nan-matrix": BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(stage + 4), float.NaN); break;
        }
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32));
        Assert.Throws<InvalidDataException>(() => Decode(bytes));
    }

    [Fact]
    public void SpecializationComposesStaticStagesAndFourExactTexelLoadsWithoutFilteringOrAnInterpreter()
    {
        var data = Data(DisplayTransformStage.Matrix(new(.5593711f, .30478334f, .13584556f), new(0, 1, 0), new(0, 0, 1), default),
            DisplayTransformStage.Log(2), DisplayTransformStage.Lut3D(2, Cube()),
            DisplayTransformStage.Exponent(new(2.4f)), DisplayTransformStage.MonCurve(new(2.4f), new(.055f), inverse: true));
        var previous = CultureInfo.CurrentCulture;
        string source;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); source = DisplayTransformShaderSource.Compile(data, "test/display", group: 1, firstBinding: 5); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Contains("@group(1) @binding(5)", source); Assert.Contains("texture_3d<f32>", source);
        Assert.Equal(4, Regex.Matches(source, @"\btextureLoad\(").Count);
        Assert.DoesNotContain("textureSample", source); Assert.DoesNotContain("sampler", source);
        Assert.DoesNotContain(".zyx +", source); Assert.DoesNotContain("switch", source);
        Assert.DoesNotContain("for (", source); Assert.Contains("dot(vec3<f32>(0.5593711", source);
        Assert.Contains("log2(max(v", source); Assert.Contains("1.1754943508222875e-38", source);
        var entry = "#import test/display\n@fragment fn main() -> @location(0) vec4<f32> { return vec4<f32>(rendering_display_transform(vec3<f32>(0.18)), 1.0); }";
        var composed = RenderingShaderSource.CompileResolved(entry, (module, _) => module == "test/display" ? source : null);
        Assert.DoesNotContain("#import", composed); Assert.Contains("textureLoad(", composed);
        Assert.Throws<ArgumentException>(() => DisplayTransformShaderSource.Compile(data, "test/display\nmalicious"));
    }
}
