using System.Runtime.InteropServices;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrPointShadowTests
{
    private static float4 Clip(float4x4 matrix, float3 point) => math.mul(matrix, new float4(point, 1));

    private static float3 Ndc(float4 clip) => clip.xyz / clip.w;

    private static void Close(float expected, float actual, float tolerance = .00001f)
        => Assert.InRange(MathF.Abs(expected - actual), 0, tolerance);

    [Theory]
    [InlineData(0, 1, 0, 0)]
    [InlineData(1, -1, 0, 0)]
    [InlineData(2, 0, 1, 0)]
    [InlineData(3, 0, -1, 0)]
    [InlineData(4, 0, 0, 1)]
    [InlineData(5, 0, 0, -1)]
    public void EachAxisProjectsIntoItsOwnFaceCenterAndRejectsTheOppositeFace(
        int face, float x, float y, float z)
    {
        var direction = new float3(x, y, z);
        Assert.Equal(direction, PbrPointShadow.Direction(face));
        Assert.Equal(face, PbrPointShadow.Face(direction));
        var matrix = PbrPointShadow.ViewProjection(float3.zero, face, 10);
        var center = Clip(matrix, direction);
        Assert.True(center.w > 0);
        var ndc = Ndc(center);
        Close(0, ndc.x); Close(0, ndc.y);
        Assert.InRange(ndc.z, 0, 1);
        Assert.True(Clip(matrix, -direction).w < 0);
    }

    [Theory]
    [InlineData(4, 1, -.5f, 0)]
    [InlineData(-4, 1, .5f, 1)]
    [InlineData(.5f, 4, -1, 2)]
    [InlineData(-.5f, -4, 1, 3)]
    [InlineData(1, .5f, 4, 4)]
    [InlineData(-1, -.5f, -4, 5)]
    public void NoncentralMajorAxisRaysStayInsideTheSelectedNinetyDegreeFace(
        float x, float y, float z, int expectedFace)
    {
        var ray = new float3(x, y, z);
        var face = PbrPointShadow.Face(ray);
        Assert.Equal(expectedFace, face);
        var clip = Clip(PbrPointShadow.ViewProjection(float3.zero, face, 50), ray);
        Assert.True(clip.w > 0);
        var ndc = Ndc(clip);
        Assert.InRange(ndc.x, -1, 1);
        Assert.InRange(ndc.y, -1, 1);
        Assert.InRange(ndc.z, 0, 1);
    }

    [Theory]
    [InlineData(1, 1, 1, 0)]
    [InlineData(-1, 1, 1, 1)]
    [InlineData(0, 1, 1, 2)]
    [InlineData(0, -1, 1, 3)]
    [InlineData(0, 0, 1, 4)]
    [InlineData(0, 0, -1, 5)]
    public void FaceTiesHaveADeterministicXThenYOrder(float x, float y, float z, int expected)
        => Assert.Equal(expected, PbrPointShadow.Face(new(x, y, z)));

    [Fact]
    public void FiniteLargeRaysDoNotNeedASquaredLengthThatCouldOverflow()
        => Assert.Equal(0, PbrPointShadow.Face(new(float.MaxValue, 1, -1)));

    [Fact]
    public void MovingThePointAndReceiverTogetherPreservesAllFaceProjections()
    {
        var position = new float3(12.25f, -7.5f, 20.125f);
        for (var face = 0; face < PbrPointShadow.Faces; face++) {
            var local = PbrPointShadow.Direction(face) * 2 + new float3(.125f, -.25f, .375f);
            var original = Ndc(Clip(PbrPointShadow.ViewProjection(float3.zero, face, 20), local));
            var moved = Ndc(Clip(PbrPointShadow.ViewProjection(position, face, 20), position + local));
            Close(original.x, moved.x); Close(original.y, moved.y); Close(original.z, moved.z);
        }
    }

    [Fact]
    public void CubeProjectionUsesTheExistingSpotNearPlaneAndAuthoredShadowRange()
    {
        var direction = PbrPointShadow.Direction(4);
        var matrix = PbrPointShadow.ViewProjection(float3.zero, 4, 10);
        Assert.True(Ndc(Clip(matrix, direction * .025f)).z < 0);
        Assert.InRange(Ndc(Clip(matrix, direction)).z, 0, 1);
        Assert.True(Ndc(Clip(matrix, direction * 12)).z > 1);
    }

    [Fact]
    public void InvalidCubeInputsFailBeforePublishingAView()
    {
        foreach (var ray in new[] { float3.zero, new float3(float.NaN, 0, 1),
            new float3(0, float.PositiveInfinity, 1), new float3(0, 1, float.NegativeInfinity) })
            Assert.Throws<ArgumentException>(() => PbrPointShadow.Face(ray));
        foreach (var range in new[] { -1f, 0f, .05f, float.NaN, float.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => PbrPointShadow.ViewProjection(float3.zero, 0, range));
        Assert.Throws<ArgumentException>(() => PbrPointShadow.ViewProjection(new(float.NaN, 0, 0), 0, 10));
        foreach (var face in new[] { -1, 6 }) {
            Assert.Throws<ArgumentOutOfRangeException>(() => PbrPointShadow.Direction(face));
            Assert.Throws<ArgumentOutOfRangeException>(() => PbrPointShadow.ViewProjection(float3.zero, face, 10));
        }
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(1, 13)]
    [InlineData(2, 19)]
    [InlineData(3, 25)]
    [InlineData(4, 31)]
    public void CapacityKeepsExistingDirectionalAndSpotLayersPlusCompleteCubes(int lights, int layers)
        => Assert.Equal(layers, PbrPointShadow.LayerCount(lights));

    [Fact]
    public void PointCapacityIsExplicitAndBounded()
    {
        Assert.Equal(0, new ShadowAtlasConfig().MaxShadowedPointLights);
        Assert.Throws<ArgumentOutOfRangeException>(() => PbrPointShadow.LayerCount(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PbrPointShadow.LayerCount(5));
    }

    [Fact]
    public void FiveBitDrawAddressesKeepEveryLayerAndInstanceDisjoint()
    {
        var packed = new HashSet<uint>();
        foreach (var instance in new[] { 0u, 1u, 31u, uint.MaxValue >> 5 })
            for (uint layer = 0; layer < 32; layer++) {
                var value = PbrPointShadow.PackInstance(instance, layer);
                Assert.True(packed.Add(value));
                Assert.Equal(instance, value >> 5);
                Assert.Equal(layer, value & 31u);
            }
        Assert.Equal(uint.MaxValue, PbrPointShadow.PackInstance(uint.MaxValue >> 5, 31));
        Assert.Throws<ArgumentOutOfRangeException>(() => PbrPointShadow.PackInstance(0, 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => PbrPointShadow.PackInstance((uint.MaxValue >> 5) + 1, 0));
    }

    [Theory]
    [InlineData(7, 96ul)]
    [InlineData(13, 168ul)]
    [InlineData(31, 384ul)]
    public void CacheAdmissionCountsEveryPersistentLayerAndItsSeparateScratch(int layers, ulong bytes)
    {
        // 65 distinct source-instance node ordinals span three words per plane.
        Assert.Equal(3u, PbrGpuHierarchy.ShadowCacheWords(65, bytes, layers));
        Assert.Equal(0u, PbrGpuHierarchy.ShadowCacheWords(65, bytes - 1, layers));
    }

    [Theory]
    [InlineData(7, 1672ul)]
    [InlineData(13, 2632ul)]
    [InlineData(19, 3592ul)]
    [InlineData(25, 4552ul)]
    [InlineData(31, 5512ul)]
    public void SelectionBudgetIncludesEveryRealConfigurationAndTheMainView(int layers, ulong bytes)
        => Assert.Equal(bytes, PbrGpuHierarchy.SelectionBufferBytes(4, 8,
            (ulong)Marshal.SizeOf<PbrGpuHierarchy.Configuration>(), layers));

    [Fact]
    public void ShadowMetadataExtendsOnlyThePrivateSelectionUniform()
    {
        Assert.Equal(160, Marshal.SizeOf<PbrGpuHierarchy.Configuration>());
        Assert.Equal(144, Marshal.OffsetOf<PbrGpuHierarchy.Configuration>("<Shadow>k__BackingField").ToInt32());
        Assert.Equal(512, Marshal.SizeOf<PbrFrame>());
    }

    [Theory]
    [InlineData(10f)]
    [InlineData(40f)]
    [InlineData(2000f)]
    public void PointDepthCoefficientMatchesTheActualPerspectiveProjection(float range)
    {
        Assert.Equal(.05f, PbrPointShadow.NearPlane);
        var matrix = PbrPointShadow.ViewProjection(float3.zero, 4, range);
        var one = Ndc(Clip(matrix, new(0, 0, 1))).z;
        var two = Ndc(Clip(matrix, new(0, 0, 2))).z;
        // For z(d)=A-B/d, z(2)-z(1)=B/2. This derives B from
        // projected geometry rather than repeating its near/far formula.
        Close(2 * (two - one), PbrPointShadow.DepthCoefficient(range), .0000002f);
    }

    [Theory]
    [InlineData(.2f, .001f)]
    [InlineData(.2f, .003f)]
    [InlineData(2f, .001f)]
    [InlineData(2f, .003f)]
    [InlineData(10f, .001f)]
    [InlineData(10f, .003f)]
    [InlineData(30f, .001f)]
    [InlineData(30f, .003f)]
    public void ConvertedWorldOffsetMatchesAMovedReceiverOnEveryCubeFace(float distance, float worldOffset)
    {
        const float range = 40;
        var coefficient = PbrPointShadow.DepthCoefficient(range);
        for (var face = 0; face < PbrPointShadow.Faces; face++) {
            // Off-axis rays distinguish radial distance from the projection's
            // axial distance. Moving along the ray preserves the sampled UV.
            var direction = math.normalize(PbrPointShadow.Direction(face) + new float3(.25f, .125f, -.1f));
            var receiver = direction * distance;
            Assert.Equal(face, PbrPointShadow.Face(receiver));
            var movedReceiver = receiver - direction * worldOffset;
            var matrix = PbrPointShadow.ViewProjection(float3.zero, face, range);
            var original = Ndc(Clip(matrix, receiver));
            var moved = Ndc(Clip(matrix, movedReceiver));
            Close(original.x, moved.x); Close(original.y, moved.y);
            var axis = System.Math.Max(System.Math.Abs((double)receiver.x),
                System.Math.Max(System.Math.Abs((double)receiver.y), System.Math.Abs((double)receiver.z)));
            var radial = System.Math.Sqrt((double)receiver.x * receiver.x
                + (double)receiver.y * receiver.y + (double)receiver.z * receiver.z);
            var fraction = System.Math.Min(worldOffset / radial, .5);
            var depthBias = coefficient * fraction / (axis * (1 - fraction));
            Assert.True(depthBias > 0);
            // Float32 projection can quantize sub-ULP far-receiver changes;
            // allow a small rounding budget in normalized depth.
            Close(moved.z, (float)(original.z - depthBias), .0000002f);
        }
    }

    [Fact]
    public void WorldBiasKeepsAMetreScaleBlockerThatTheOldFixedDepthBiasLoses()
    {
        var matrix = PbrPointShadow.ViewProjection(float3.zero, 4, 40);
        var blocker = Ndc(Clip(matrix, new(0, 0, 8))).z;
        var receiver = Ndc(Clip(matrix, new(0, 0, 10))).z;
        Assert.True(receiver > blocker);
        // LessEqual comparison would wrongly report this ten-metre receiver
        // as lit behind the eight-metre blocker after the former .0015 offset.
        Assert.True(receiver - .0015f <= blocker);
        var fraction = .003 / 10;
        var bias = PbrPointShadow.DepthCoefficient(40) * fraction / (10 * (1 - fraction));
        Assert.True(receiver - bias > blocker);
        var shifted = Ndc(Clip(matrix, new(0, 0, 10 - .003f))).z;
        Close(shifted, (float)(receiver - bias), .0000002f);
    }

    [Fact]
    public void ReceiverBiasDefaultsAreSmallWorldDistancesAndInvalidPoliciesAreRejected()
    {
        var defaults = new ShadowAtlasConfig();
        Assert.Equal(.001f, defaults.PointShadowConstantBias);
        Assert.Equal(.002f, defaults.PointShadowSlopeBias);
        PbrPointShadow.ValidateBias(defaults.PointShadowConstantBias, defaults.PointShadowSlopeBias);
        PbrPointShadow.ValidateBias(0, 0);
        foreach (var invalid in new[] { -.001f, float.NaN, float.PositiveInfinity, float.NegativeInfinity }) {
            Assert.Throws<ArgumentException>(() => PbrPointShadow.ValidateBias(invalid, 0));
            Assert.Throws<ArgumentException>(() => PbrPointShadow.ValidateBias(0, invalid));
        }
        foreach (var range in new[] { -.001f, 0f, .05f, float.NaN, float.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => PbrPointShadow.DepthCoefficient(range));
    }
}
