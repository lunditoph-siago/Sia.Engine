using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Sia;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.WebGPU;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrHdrStorageTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static void Set(object owner, string name, object value)
        => (owner.GetType().GetField(name, Instance)
            ?? owner.GetType().GetField("<" + name + ">k__BackingField", Instance))!.SetValue(owner, value);

    private static PbrRenderer CpuOwner(bool wide)
    {
        // Skip only device construction; admission and format selection are real.
        var owner = (PbrRenderer)RuntimeHelpers.GetUninitializedObject(typeof(PbrRenderer));
        Set(owner, nameof(PbrRenderer.FullPrecisionHdr), wide);
        return owner;
    }

    private static float StorageScale(PbrRenderer owner, double peak)
    {
        try { return (float)typeof(PbrRenderer).GetMethod("StorageScale", Instance)!.Invoke(owner, [peak, "Test"])!; }
        catch (TargetInvocationException error) when (error.InnerException is { } inner) {
            ExceptionDispatchInfo.Capture(inner).Throw(); throw;
        }
    }

    [Theory]
    [InlineData(false, WGPUTextureFormat.RGBA16Float, 8u)]
    [InlineData(true, WGPUTextureFormat.RGBA32Float, 16u)]
    public void EnabledStorageCapabilityMatchesTheActualFormatAndByteCost(bool wide, WGPUTextureFormat format, uint bytes)
    {
        var owner = CpuOwner(wide);
        Assert.Equal(format, owner.HdrFormat);
        Assert.Equal(bytes, owner.HdrBytesPerTexel);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 32752)]
    [InlineData(true, 0)]
    [InlineData(true, 32752)]
    [InlineData(true, 1000000000000d)]
    public void AdmittedHdrRetainsPhysicalRadianceInsteadOfAConservativeGlobalScale(bool wide, double peak)
        => Assert.Equal(1f, StorageScale(CpuOwner(wide), peak));

    [Theory]
    [InlineData(32752.0001)]
    [InlineData(65504)]
    [InlineData(1000000000000d)]
    public void HalfCannotSilentlyScaleAHighDynamicRangeSceneIntoBlack(double peak)
    {
        var error = Assert.Throws<NotSupportedException>(() => StorageScale(CpuOwner(false), peak));
        Assert.Contains("Float32Blendable", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOrUnrepresentableWorkingBoundsStillFailBeforeStorageSelection(bool wide)
    {
        var owner = CpuOwner(wide);
        foreach (var peak in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, float.MaxValue / 2048d })
            Assert.Throws<ArgumentException>(() => StorageScale(owner, peak));
    }

    [Fact]
    public void WideStorageKeepsTheExistingFiniteFloat32WorkingAdmission()
        => Assert.Equal(1f, StorageScale(CpuOwner(true), float.MaxValue / 4096d));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnvironmentCubeDecodeScaleDoesNotSetTheFrameAttachmentScale(bool wide)
    {
        var owner = CpuOwner(wide);
        var environment = (IblEnvironmentGpu)RuntimeHelpers.GetUninitializedObject(typeof(IblEnvironmentGpu));
        Set(environment, "_proceduralScale", 67108864f);
        Set(owner, "Environment", environment);
        Set(owner, "Settings", new PbrRendererSettings());
        Assert.Equal(67108864f, environment.CubeRadianceScale);
        Assert.Equal(1f, owner.FrameRadianceScale(0));
    }

    [Theory]
    [InlineData("forward.wgsl", false)]
    [InlineData("forward.wgsl", true)]
    [InlineData("transparent.wgsl", false)]
    [InlineData("transparent.wgsl", true)]
    [InlineData("resolve.wgsl", false)]
    [InlineData("resolve.wgsl", true)]
    [InlineData("reflection_compose.wgsl", false)]
    [InlineData("reflection_compose.wgsl", true)]
    public void EveryRadianceWriterComposesTheSelectedStorageArithmetic(string entry, bool wide)
    {
        using var world = new World();
        var source = PbrShaderSource.Compile(world, entry, fullPrecisionHdr: wide);
        Assert.DoesNotContain("#if", source); Assert.DoesNotContain("#{", source);
        var function = Regex.Match(source, @"fn\s+\w*pbr_store_hdr\s*\([^{}]*\)\s*->\s*vec3<f32>\s*\{(?<body>[^{}]*)\}");
        Assert.True(function.Success, "The selected radiance-storage function must be composed from its builtin module.");
        var body = function.Groups["body"].Value;
        Assert.Equal(!wide, body.Contains("65504", StringComparison.Ordinal));
        if (wide) Assert.Contains("return radiance * inverse_scale;", body);
        // The helper must be used by the entry, rather than only imported.
        Assert.True(Regex.Matches(source, @"\b\w*pbr_store_hdr\s*\(").Count >= 2);
    }

    [Theory]
    [InlineData("resolve.wgsl", false)]
    [InlineData("resolve.wgsl", true)]
    [InlineData("forward_background.wgsl", false)]
    [InlineData("forward_background.wgsl", true)]
    [InlineData("reflection_compose.wgsl", false)]
    [InlineData("reflection_compose.wgsl", true)]
    public void ComputeHdrStorageDeclarationsMatchTheSelectedAttachment(string entry, bool wide)
    {
        using var world = new World();
        var source = PbrShaderSource.Compile(world, entry, fullPrecisionHdr: wide);
        var declaration = Regex.Match(source, @"var\s+\w*hdr\s*:\s*texture_storage_2d<(?<format>rgba(?:16|32)float),\s*write>");
        Assert.True(declaration.Success, "The entry must declare its actual HDR storage target.");
        Assert.Equal(wide ? "rgba32float" : "rgba16float", declaration.Groups["format"].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReflectionOpaquePayloadRemainsPackedFloat32IndependentOfTheFinalHdrFormat(bool wide)
    {
        using var world = new World();
        var source = PbrShaderSource.Compile(world, "resolve.wgsl", surfaceData: true, sceneGi: true,
            streamInstances: true, shadingWork: true, liveReflections: true, fullPrecisionHdr: wide);
        Assert.Matches(@"var\s+\w*hdr\s*:\s*texture_storage_2d_array<rgba8unorm,\s*write>", source);
        Assert.Contains("reflection_pack_float", source);
        Assert.DoesNotContain("#if", source); Assert.DoesNotContain("#{", source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplayAndExposureLoadExactHdrTexelsWithoutFloat32Filtering(bool wide)
    {
        using var world = new World();
        foreach (var entry in new[] { "output.wgsl", "exposure.wgsl" }) {
            var source = PbrShaderSource.Compile(world, entry, fullPrecisionHdr: wide);
            Assert.Contains("textureLoad(", source);
            Assert.DoesNotContain("textureSampleLevel(", source);
            Assert.DoesNotContain("textureSample(", source);
            Assert.DoesNotContain("#if", source); Assert.DoesNotContain("#{", source);
        }
        // These are CPU composition checks. Actual unfilterable binding layouts,
        // blending support and render-target validation remain GPU smoke gates.
    }
}
