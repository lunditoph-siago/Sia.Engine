using Sia.WebGPU;

namespace Sia.Engine.Rendering;

/// <summary>Storage and copy units for the texture formats accepted by the rendering upload path.</summary>
public readonly record struct TextureBlockLayout
{
    public uint BlockWidth { get; }
    public uint BlockHeight { get; }
    public uint BytesPerBlock { get; }

    private TextureBlockLayout(uint width, uint height, uint bytes)
        => (BlockWidth, BlockHeight, BytesPerBlock) = (width, height, bytes);

    public static TextureBlockLayout For(WGPUTextureFormat format) => format switch {
        WGPUTextureFormat.RGBA8Unorm or WGPUTextureFormat.RGBA8UnormSrgb => new(1, 1, 4),
        WGPUTextureFormat.BC7RGBAUnorm or WGPUTextureFormat.BC7RGBAUnormSrgb
            or WGPUTextureFormat.BC5RGUnorm => new(4, 4, 16),
        _ => throw new NotSupportedException($"Texture format {format} is not supported by this upload path.")
    };

    public uint BlockColumns(uint width)
    {
        ArgumentOutOfRangeException.ThrowIfZero(width);
        if (BytesPerBlock == 0) throw new InvalidOperationException("An initialized texture block layout is required.");
        return 1 + (width - 1) / BlockWidth;
    }

    public uint BlockRows(uint height)
    {
        ArgumentOutOfRangeException.ThrowIfZero(height);
        if (BytesPerBlock == 0) throw new InvalidOperationException("An initialized texture block layout is required.");
        return 1 + (height - 1) / BlockHeight;
    }

    public uint RowBytes(uint width) => checked(BlockColumns(width) * BytesPerBlock);

    public ulong MipByteLength(uint width, uint height)
        => checked((ulong)RowBytes(width) * BlockRows(height));

    /// <summary>Base textures require block-aligned dimensions; smaller mip copies use rounded physical extents.</summary>
    public bool SupportsBaseDimensions(uint width, uint height)
        => width != 0 && height != 0 && BytesPerBlock != 0
            && width % BlockWidth == 0 && height % BlockHeight == 0;
}
