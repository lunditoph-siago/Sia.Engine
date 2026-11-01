using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public sealed unsafe class TextureArrayUpload : IDisposable
{
    private readonly GpuResources _gpu;
    private ReadOnlyMemory<byte>[][] _levels;
    private readonly uint _width, _height;
    private readonly TextureBlockLayout _layout;
    private readonly Entity _previous;
    private readonly int _sourceMipOffset, _targetMipOffset, _sharedMipCount, _layers;
    private int _copyMip, _copyLayer;
    private uint _copyBlock;
    private int _layer, _mip;
    private uint _block;
    private bool _aborted;

    public Entity Texture { get; }
    public Entity View { get; }

    public ulong Bytes => _gpu.Bytes;
    public uint CopiedBytes { get; private set; }

    public bool Complete => _levels.Length == 0 && _copyMip == _sharedMipCount && !_aborted;

    public TextureArrayUpload(in GpuFrame frame, uint width, uint height, bool srgb,
        ReadOnlyMemory<byte>[][] levels, ulong budget)
        : this(frame, width, height, srgb ? WGPUTextureFormat.RGBA8UnormSrgb : WGPUTextureFormat.RGBA8Unorm,
            levels, budget) { }

    public TextureArrayUpload(in GpuFrame frame, uint width, uint height, WGPUTextureFormat format,
        ReadOnlyMemory<byte>[][] levels, ulong budget)
        : this(frame, width, height, format, levels, budget, default, 0, null, default, false) { }

    public TextureArrayUpload(in GpuFrame frame, uint width, uint height, bool srgb,
        ReadOnlyMemory<byte>[][] levels, ulong budget, Entity previous,
        int sharedMipOffset, int mipCount)
        : this(frame, width, height, srgb ? WGPUTextureFormat.RGBA8UnormSrgb : WGPUTextureFormat.RGBA8Unorm,
            levels, budget, previous, sharedMipOffset, mipCount) { }

    public TextureArrayUpload(in GpuFrame frame, uint width, uint height, WGPUTextureFormat format,
        ReadOnlyMemory<byte>[][] levels, ulong budget, Entity previous,
        int sharedMipOffset, int mipCount)
        : this(frame, width, height, format, levels, budget, previous, sharedMipOffset, mipCount, default, false) { }

    /// <summary>Writes unpublished finer mips into an existing texture; the caller retains its ownership.</summary>
    public static TextureArrayUpload Update(in GpuFrame frame, uint width, uint height,
        Entity texture, Entity view, ReadOnlyMemory<byte>[][] levels)
        => Update(frame, width, height, texture, view, texture.Get<WgpuTextureInfo>().Format, levels);

    public static TextureArrayUpload Update(in GpuFrame frame, uint width, uint height,
        Entity texture, Entity view, WGPUTextureFormat format, ReadOnlyMemory<byte>[][] levels)
        => new(frame, width, height, format, levels, 0, texture, 0, null, view, true);

    private TextureArrayUpload(in GpuFrame frame, uint width, uint height, WGPUTextureFormat format,
        ReadOnlyMemory<byte>[][] levels, ulong budget, Entity previous, int sharedMipOffset,
        int? mipCount, Entity existingView, bool updateExisting)
    {
        _layout = TextureBlockLayout.For(format);
        var count = mipCount ?? (levels.Length == 0 ? 0 : levels[0].Length);
        var shared = previous.IsValid;
        if (width == 0 || height == 0 || levels.Length == 0 || count is < 1 or > 14
            || (count > 1 && (width >> (count - 2)) <= 1 && (height >> (count - 2)) <= 1)
            || (!shared && levels[0].Length != count)
            || (shared && (sharedMipOffset is < -13 or > 13
                || (levels[0].Length != System.Math.Max(0, sharedMipOffset) && levels[0].Length != count)
                || sharedMipOffset >= count)))
            throw new ArgumentException("Texture upload requires complete nonempty mip tails.", nameof(levels));
        if (!_layout.SupportsBaseDimensions(width, height))
            throw new ArgumentException("Texture base dimensions must align to its storage blocks.", nameof(width));
        if (shared) {
            var previousInfo = previous.Get<WgpuTextureInfo>();
            var sourceOffset = System.Math.Max(0, -sharedMipOffset);
            var targetOffset = System.Math.Max(0, sharedMipOffset);
            if (previousInfo.Format != format || previousInfo.Size.DepthOrArrayLayers != (uint)levels.Length
                || previousInfo.Dimension != WGPUTextureDimension._2D
                || previousInfo.MipLevelCount < (uint)(sourceOffset + count - targetOffset)
                || System.Math.Max(1u, previousInfo.Size.Width >> sourceOffset) != System.Math.Max(1u, width >> targetOffset)
                || System.Math.Max(1u, previousInfo.Size.Height >> sourceOffset) != System.Math.Max(1u, height >> targetOffset)
                || (previousInfo.Usage & (updateExisting ? WGPUTextureUsage.CopyDst : WGPUTextureUsage.CopySrc)) == 0)
                throw new ArgumentException("Shared texture format, layers or mip range differ.", nameof(previous));
        }
        ulong bytes = 0;
        foreach (var layer in levels) {
            if (layer.Length != levels[0].Length)
                throw new ArgumentException("Array mip counts differ.", nameof(levels));
            for (var mip = 0; mip < count; mip++) {
                var expected = _layout.MipByteLength(System.Math.Max(1u, width >> mip), System.Math.Max(1u, height >> mip));
                if (mip < layer.Length && (!shared || !layer[mip].IsEmpty) && (ulong)layer[mip].Length != expected)
                    throw new ArgumentException("Texture mip byte count mismatch.", nameof(levels));
                bytes = checked(bytes + expected);
            }
        }
        (_width, _height, _levels) = (width, height, levels);
        _layers = levels.Length;
        _previous = previous;
        _sourceMipOffset = System.Math.Max(0, -sharedMipOffset);
        _targetMipOffset = System.Math.Max(0, sharedMipOffset);
        _sharedMipCount = shared && !updateExisting ? count - _targetMipOffset : 0;
        if (levels[0].Length == 0) _levels = [];
        _gpu = new(frame, budget);
        try {
            if (updateExisting) {
                if (!previous.IsValid || !existingView.IsValid) throw new ArgumentException("Existing texture and view are required.");
                Texture = previous; View = existingView;
                return;
            }
            var binding = WGPUTextureBindingViewDimension.Default;
            binding.TextureBindingViewDimension = WGPUTextureViewDimension._2DArray;
            var descriptor = WGPUTextureDescriptor.Default;
            descriptor.NextInChain = &binding.Chain;
            descriptor.Dimension = WGPUTextureDimension._2D;
            descriptor.Size = new() {
                Width = width,
                Height = height,
                DepthOrArrayLayers = (uint)levels.Length
            };
            descriptor.Format = format;
            descriptor.MipLevelCount = (uint)count;
            descriptor.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst | WGPUTextureUsage.CopySrc;
            Texture = _gpu.Texture(descriptor, bytes);
            var view = WGPUTextureViewDescriptor.Default;
            view.Dimension = WGPUTextureViewDimension._2DArray;
            View = _gpu.Own(Wgpu.CreateTextureView(Texture.GetWgpu<WGPUTexture>(), view));
        }
        catch { _gpu.Dispose(); throw; }
    }

    public uint Advance(uint budget)
    {
        if (_aborted) throw new InvalidOperationException("An abandoned texture upload cannot resume.");
        CopiedBytes = CopyShared(budget);
        uint uploaded = CopiedBytes;
        while (_levels.Length != 0) {
            if (_levels[_layer][_mip].IsEmpty) { NextMip(); continue; }
            if (budget - uploaded < _layout.BytesPerBlock) break;
            var width = System.Math.Max(1u, _width >> _mip);
            var height = System.Math.Max(1u, _height >> _mip);
            var region = Region(width, height, _block, budget - uploaded);
            var destination = new WGPUTexelCopyTextureInfo {
                Texture = (WGPUTexture*)Texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                MipLevel = (uint)_mip,
                Origin = new() { X = region.X, Y = region.Y, Z = (uint)_layer },
                Aspect = WGPUTextureAspect.All
            };
            var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = region.RowBytes, RowsPerImage = region.Rows };
            var size = new WGPUExtent3D { Width = region.Width, Height = region.Height, DepthOrArrayLayers = 1 };
            fixed (byte* data = _levels[_layer][_mip].Span) {
                WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)_gpu.Queue.DangerousGetHandle(), &destination,
                    data + checked((nint)((ulong)_block * _layout.BytesPerBlock)), region.Bytes, &layout, &size);
            }
            uploaded += region.Bytes;
            _block += region.Blocks;
            if (_block == _layout.BlockColumns(width) * _layout.BlockRows(height)) {
                NextMip();
            }
        }
        return uploaded;
    }

    private void NextMip()
    {
        _block = 0;
        if (++_mip == _levels[_layer].Length) { _mip = 0; _layer++; }
        if (_layer == _levels.Length) _levels = [];
    }

    private uint CopyShared(uint budget)
    {
        if (_copyMip == _sharedMipCount || budget < _layout.BytesPerBlock) return 0;
        var encoder = Wgpu.CreateCommandEncoder(_gpu.Device);
        uint copied = 0;
        try {
            while (_copyMip < _sharedMipCount && budget - copied >= _layout.BytesPerBlock) {
                var mip = _targetMipOffset + _copyMip;
                var width = System.Math.Max(1u, _width >> mip);
                var height = System.Math.Max(1u, _height >> mip);
                var region = Region(width, height, _copyBlock, budget - copied);
                var source = new WGPUTexelCopyTextureInfo {
                    Texture = (WGPUTexture*)_previous.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                    MipLevel = (uint)(_sourceMipOffset + _copyMip),
                    Origin = new() { X = region.X, Y = region.Y, Z = (uint)_copyLayer }, Aspect = WGPUTextureAspect.All
                };
                var destination = new WGPUTexelCopyTextureInfo {
                    Texture = (WGPUTexture*)Texture.GetWgpu<WGPUTexture>().DangerousGetHandle(), MipLevel = (uint)mip,
                    Origin = source.Origin, Aspect = WGPUTextureAspect.All
                };
                var extent = new WGPUExtent3D { Width = region.Width, Height = region.Height, DepthOrArrayLayers = 1 };
                WgpuUnsafe.wgpuCommandEncoderCopyTextureToTexture((WGPUCommandEncoder*)encoder.DangerousGetHandle(), &source, &destination, &extent);
                copied += region.Bytes; _copyBlock += region.Blocks;
                if (_copyBlock == _layout.BlockColumns(width) * _layout.BlockRows(height)) {
                    _copyBlock = 0;
                    if (++_copyLayer == _layers) { _copyLayer = 0; _copyMip++; }
                }
            }
            var command = Wgpu.FinishCommandEncoder(encoder, WGPUCommandBufferDescriptor.Default);
            try { Wgpu.Submit(_gpu.Queue, [command]); }
            finally { Wgpu.Release(ref command); }
        }
        finally { Wgpu.Release(ref encoder); }
        return copied;
    }

    private (uint X, uint Y, uint Width, uint Height, uint RowBytes, uint Rows, uint Bytes, uint Blocks)
        Region(uint width, uint height, uint block, uint budget)
    {
        var columns = _layout.BlockColumns(width); var rows = _layout.BlockRows(height);
        var x = block % columns; var y = block / columns;
        var capacity = budget / _layout.BytesPerBlock;
        var copyRows = x == 0 && capacity >= columns ? System.Math.Min(rows - y, capacity / columns) : 1u;
        var copyColumns = copyRows > 1 ? columns : System.Math.Min(columns - x, capacity);
        var rowBytes = copyColumns * _layout.BytesPerBlock;
        // WebGPU copies address the rounded physical mip extent, including the final partial block.
        return (x * _layout.BlockWidth, y * _layout.BlockHeight,
            copyColumns * _layout.BlockWidth, copyRows * _layout.BlockHeight,
            rowBytes, copyRows, rowBytes * copyRows, copyColumns * copyRows);
    }

    public void Abandon()
    {
        _levels = [];
        _aborted = true;
    }

    public void Dispose()
    {
        Abandon();
        _gpu.Dispose();
    }
}
