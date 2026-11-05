using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.WebGPU;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class TextureStreamingPolicyTests
{
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
