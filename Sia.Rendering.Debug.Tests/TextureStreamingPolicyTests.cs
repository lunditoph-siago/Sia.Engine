using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.WebGPU;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class TextureStreamingPolicyTests
{
    [Fact]
    public void ElapsedTimeUploadCreditIsBoundedByTheExplicitFrameLimit()
    {
        double credit = 0;
        Assert.Equal(400u, PbrTextureStreamingPolicy.UploadAllowance(1024, 4000, 0.1, ref credit));
        credit -= 400;
        Assert.Equal(200u, PbrTextureStreamingPolicy.UploadAllowance(1024, 4000, 0.05, ref credit));
        Assert.Equal(600u, PbrTextureStreamingPolicy.UploadAllowance(1024, 4000, 60, ref credit));
        Assert.Equal(1024u, PbrTextureStreamingPolicy.UploadAllowance(1024, 40000, 0.1, ref credit));
        Assert.Equal(16u, PbrTextureStreamingPolicy.UploadAllowance(16, 40000, 0.1, ref credit));
        Assert.Equal(4096u, PbrTextureStreamingPolicy.UploadAllowance(4096, 0, 0, ref credit));
    }

    [Fact]
    public void TransientDemandChangesDoNotCancelAnAdmittedRead()
    {
        var frames = 0;
        Assert.False(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref frames));
        Assert.False(PbrTextureStreamingPolicy.StableObsolete(false, 3, ref frames));
        Assert.Equal(0, frames);
        Assert.False(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref frames));
        Assert.False(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref frames));
        Assert.True(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref frames));
        Assert.True(PbrTextureStreamingPolicy.StableObsolete(true, 3, ref frames));
        Assert.Equal(3, frames);
    }

    [Fact]
    public void DefaultDecodedReservationAdmitsOneFourKRawMip()
    {
        var settings = new PbrTextureStreamingSettings();
        var raw = TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm);
        Assert.Equal(64L * 1024 * 1024, settings.DecodedBytes);
        Assert.Equal(0, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 6, 8192, settings.DecodedBytes, raw));
    }

    [Theory]
    [InlineData(WGPUTextureFormat.BC7RGBAUnormSrgb)]
    [InlineData(WGPUTextureFormat.BC5RGUnorm)]
    public void LargerCpuDecodedBudgetKeepsFourKCompressedLayersIndependent(WGPUTextureFormat format)
    {
        var layout = TextureBlockLayout.For(format);
        var fullLayer = Enumerable.Range(0, 13).Sum(mip => (long)layout.MipByteLength(
            System.Math.Max(1u, 4096u >> mip), System.Math.Max(1u, 4096u >> mip)));
        Assert.Equal(22_369_648L, fullLayer);
        Assert.Equal(1, PbrTextureStreamingPolicy.ArrayLayerLimit(32L * 1024 * 1024, fullLayer, 256));
        Assert.Equal(1, PbrTextureStreamingPolicy.ArrayLayerLimit(64L * 1024 * 1024, fullLayer, 256));
        Assert.Equal(1, PbrTextureStreamingPolicy.ArrayLayerLimit(256L * 1024 * 1024, fullLayer, 256));
    }

    [Fact]
    public void ArrayGroupingRetainsLowerCpuAndDeviceLayerLimits()
    {
        const long twoKCompressedLayer = 5_592_432;
        Assert.Equal(2, PbrTextureStreamingPolicy.ArrayLayerLimit(16L * 1024 * 1024, twoKCompressedLayer, 256));
        Assert.Equal(5, PbrTextureStreamingPolicy.ArrayLayerLimit(32L * 1024 * 1024, twoKCompressedLayer, 256));
        Assert.Equal(5, PbrTextureStreamingPolicy.ArrayLayerLimit(64L * 1024 * 1024, twoKCompressedLayer, 256));
        Assert.Equal(2, PbrTextureStreamingPolicy.ArrayLayerLimit(64L * 1024 * 1024, twoKCompressedLayer, 2));
    }

    [Fact]
    public void IndependentArrayGroupingDoesNotReduceOneFourKRawMipAdmission()
    {
        var layout = TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm);
        const long decoded = 64L * 1024 * 1024;
        var fullLayer = Enumerable.Range(0, 13).Sum(mip => (long)layout.MipByteLength(
            System.Math.Max(1u, 4096u >> mip), System.Math.Max(1u, 4096u >> mip)));
        Assert.Equal(89_478_484L, fullLayer);
        Assert.Equal((ulong)decoded, layout.MipByteLength(4096, 4096));
        Assert.Equal(1, PbrTextureStreamingPolicy.ArrayLayerLimit(decoded, fullLayer, 256));
        Assert.Equal(0, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 6, 8192, decoded, layout));
    }

    [Fact]
    public void FullEightKTextureCanAdmitItsTopMipWithoutDecodingItsWholeReplacement()
    {
        var raw = TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm);
        Assert.Equal(0, PbrTextureStreamingPolicy.MinimumMip(8192, 8192, 7, 8192, 256L * 1024 * 1024, raw));
        Assert.Equal(1, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 6, 8192, 32L * 1024 * 1024, raw));
    }

    [Fact]
    public void DeviceDimensionRestrictsDetailEvenWhenDecodedBudgetIsLarge()
    {
        var raw = TextureBlockLayout.For(WGPUTextureFormat.RGBA8UnormSrgb);
        Assert.Equal(2, PbrTextureStreamingPolicy.MinimumMip(4096, 2048, 6, 1024, long.MaxValue, raw));
        Assert.Throws<NotSupportedException>(() => PbrTextureStreamingPolicy.MinimumMip(4096, 2048, 0, 1024, long.MaxValue, raw));
    }

    [Fact]
    public void CompressedAdmissionUsesBlockBytesRatherThanFourBytesPerPixel()
    {
        var bc7 = TextureBlockLayout.For(WGPUTextureFormat.BC7RGBAUnormSrgb);
        Assert.Equal(0, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 6, 8192, 16L * 1024 * 1024, bc7));
        Assert.Equal(1, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 6, 2048, 16L * 1024 * 1024, bc7));
    }

    [Fact]
    public void ReturningNearViewCancelsAnUnfinishedDowngrade()
    {
        Assert.True(PbrTextureStreamingPolicy.IsObsolete([0, 4], [0, 4], [2, 4]));
        Assert.True(PbrTextureStreamingPolicy.IsObsolete([0, 4], [1, 4], [2, 4]));
        Assert.False(PbrTextureStreamingPolicy.IsObsolete([0, 4], [2, 4], [2, 4]));
    }

    [Fact]
    public void StillUsefulIntermediateUpgradeSurvivesLargerDetailDemand()
    {
        Assert.False(PbrTextureStreamingPolicy.IsObsolete([4, 4], [0, 6], [3, 4]));
        Assert.True(PbrTextureStreamingPolicy.IsObsolete([4, 4], [4, 4], [3, 4]));
    }

    [Fact]
    public void PendingAndRetiredStorageCannotWrapTheRemainingBudget()
    {
        Assert.Equal(4ul, PbrTextureStreamingPolicy.AvailableBytes(64, 48, 12));
        Assert.Equal(0ul, PbrTextureStreamingPolicy.AvailableBytes(64, 48, 16));
        Assert.Equal(0ul, PbrTextureStreamingPolicy.AvailableBytes(64, 48, ulong.MaxValue));
        Assert.Equal(0ul, PbrTextureStreamingPolicy.AvailableBytes(64, ulong.MaxValue));
        Assert.Equal(0ul, PbrTextureStreamingPolicy.AvailableBytes(ulong.MaxValue, 1, ulong.MaxValue));
    }

    [Fact]
    public void LargePartialChainRequiresAnExplicitBootstrapAllowance()
    {
        const ulong oneFourKMip = 64ul * 1024 * 1024;
        Assert.Throws<InvalidOperationException>(() => PbrTextureStreamingPolicy.ValidateBootstrap(oneFourKMip, 32ul * 1024 * 1024));
        PbrTextureStreamingPolicy.ValidateBootstrap(oneFourKMip, oneFourKMip);
        Assert.Equal(0, PbrTextureStreamingPolicy.MinimumMip(4096, 4096, 0, 8192,
            32L * 1024 * 1024, TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm)));
    }
}
