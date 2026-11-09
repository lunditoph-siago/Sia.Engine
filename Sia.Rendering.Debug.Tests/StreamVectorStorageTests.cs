using Sia;
using Sia.Engine.Rendering.Pbr;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class StreamVectorStorageTests
{
    [Theory]
    [InlineData(0ul, 0ul, 80ul)]
    [InlineData(1ul, 1ul, 80ul)]
    [InlineData(2ul, 1ul, 128ul)]
    [InlineData(3ul, 1ul, 176ul)]
    [InlineData(4ul, 1ul, 224ul)]
    [InlineData(5ul, 1ul, 288ul)]
    [InlineData(17ul, 1ul, 912ul)]
    public void PhysicalStorageIncludesLastVectorAndMinimumTopology(ulong vertices, ulong triangles, ulong bytes)
        => Assert.Equal(bytes, PbrGpuScene.StreamStorageBytes(vertices, triangles));

    [Fact]
    public void PhysicalStorageArithmeticCannotWrapTheExistingGeometryCap()
    {
        Assert.Throws<OverflowException>(() => PbrGpuScene.StreamStorageBytes(ulong.MaxValue, 1));
        Assert.Throws<OverflowException>(() => PbrGpuScene.StreamStorageBytes(1, ulong.MaxValue));
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, true, false, true)]
    public void OnlyStreamBindingsUseAlignedVectorStorage(bool stream, bool work, bool resident, bool cpu, bool raster)
    {
        using var world = new World();
        var source = PbrShaderSource.Compile(world, "bindings.wgsl", streamInstances: stream,
            shadingWork: work, residentInstances: resident, cpuStreamInstances: cpu, rasterPositionTables: raster);
        Assert.Equal(stream || work, source.Contains("vertices: array<vec4<u32>>", StringComparison.Ordinal));
        Assert.Equal(!stream && !work, source.Contains("vertices: array<u32>", StringComparison.Ordinal));
        Assert.DoesNotContain("#if", source); Assert.DoesNotContain("#{", source);
        var variants = new[] { "raster.wgsl", "resolve.wgsl", "forward.wgsl", "transparent.wgsl" }
            .Select(entry => PbrShaderSource.Compile(world, entry, streamInstances: stream,
                shadingWork: entry == "resolve.wgsl" && work, residentInstances: resident,
                cpuStreamInstances: cpu, rasterPositionTables: raster));
        foreach (var variant in variants) {
            Assert.DoesNotContain("#if", variant); Assert.DoesNotContain("#{", variant);
        }
        // Builtin composition is CPU coverage. Odd-capacity metadata and raw
        // plane words must also be checked by actual GPU readback.
    }
}
