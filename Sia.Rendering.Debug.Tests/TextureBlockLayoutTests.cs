using Sia.Engine.Rendering;
using Sia.WebGPU;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class TextureBlockLayoutTests
{
    [Theory]
    [InlineData(WGPUTextureFormat.BC7RGBAUnorm)]
    [InlineData(WGPUTextureFormat.BC7RGBAUnormSrgb)]
    [InlineData(WGPUTextureFormat.BC5RGUnorm)]
    public void TinyAndPartialMipExtentsRetainCompleteStorageBlocks(WGPUTextureFormat format)
    {
        var layout = TextureBlockLayout.For(format);
        Assert.Equal(16ul, layout.MipByteLength(1, 1));
        Assert.Equal(16ul, layout.MipByteLength(2, 3));
        Assert.Equal(64ul, layout.MipByteLength(7, 5));
        Assert.Equal(32u, layout.RowBytes(7));
        Assert.Equal(8u, layout.BlockColumns(7) * layout.BlockWidth);
        Assert.Equal(8u, layout.BlockRows(5) * layout.BlockHeight);
        Assert.False(layout.SupportsBaseDimensions(7, 5));
        Assert.True(layout.SupportsBaseDimensions(8, 8));
    }

    [Fact]
    public void FourKCompleteChainMatchesIndependentRawAndBlockPayloadTotals()
    {
        static ulong Sum(TextureBlockLayout layout)
        {
            ulong bytes = 0;
            for (uint side = 4096; ; side /= 2) {
                bytes += layout.MipByteLength(side, side);
                if (side == 1) return bytes;
            }
        }
        Assert.Equal(89_478_484ul, Sum(TextureBlockLayout.For(WGPUTextureFormat.RGBA8Unorm)));
        Assert.Equal(22_369_648ul, Sum(TextureBlockLayout.For(WGPUTextureFormat.BC7RGBAUnorm)));
    }

    [Fact]
    public void RawOddDimensionsRemainTightlyPacked()
    {
        var layout = TextureBlockLayout.For(WGPUTextureFormat.RGBA8UnormSrgb);
        Assert.Equal(140ul, layout.MipByteLength(7, 5));
        Assert.Equal(28u, layout.RowBytes(7));
        Assert.True(layout.SupportsBaseDimensions(7, 5));
    }
}
