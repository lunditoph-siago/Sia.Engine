using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public sealed unsafe class TextureArrayUpload : IDisposable
{
    private readonly GpuResources _gpu;
    private ReadOnlyMemory<byte>[][] _levels;
    private readonly uint _width, _height;
    private readonly Entity _previous;
    private readonly int _sourceMipOffset, _targetMipOffset, _sharedMipCount, _layers;
    private int _copyMip, _copyLayer, _copyPixel;
    private int _layer, _mip, _pixel;
    private bool _aborted;

    public Entity Texture { get; }
    public Entity View { get; }

    public ulong Bytes => _gpu.Bytes;
    public uint CopiedBytes { get; private set; }

    public bool Complete => _levels.Length == 0 && _copyMip == _sharedMipCount && !_aborted;

    public TextureArrayUpload(in GpuFrame frame, uint width, uint height, bool srgb,
        ReadOnlyMemory<byte>[][] levels, ulong budget)
        : this(frame, width, height, srgb, levels, budget, default, 0, null, default, false) { }

    public TextureArrayUpload(in GpuFrame frame, uint width, uint height, bool srgb,
        ReadOnlyMemory<byte>[][] levels, ulong budget, Entity previous,
        int sharedMipOffset, int mipCount)
        : this(frame, width, height, srgb, levels, budget, previous, sharedMipOffset, mipCount, default, false) { }

    /// <summary>Writes unpublished finer mips into an existing texture; the caller retains its ownership.</summary>
    public static TextureArrayUpload Update(in GpuFrame frame, uint width, uint height,
        Entity texture, Entity view, ReadOnlyMemory<byte>[][] levels)
        => new(frame, width, height, false, levels, 0, texture, 0, null, view, true);

    private TextureArrayUpload(in GpuFrame frame, uint width, uint height, bool srgb,
        ReadOnlyMemory<byte>[][] levels, ulong budget, Entity previous, int sharedMipOffset,
        int? mipCount, Entity existingView, bool updateExisting)
    {
        var count = mipCount ?? (levels.Length == 0 ? 0 : levels[0].Length);
        var shared = previous.IsValid;
        if (width == 0 || height == 0 || levels.Length == 0 || count is < 1 or > 14
            || (count > 1 && (width >> (count - 2)) <= 1 && (height >> (count - 2)) <= 1)
            || (!shared && levels[0].Length != count)
            || (shared && (sharedMipOffset is < -13 or > 13
                || (levels[0].Length != System.Math.Max(0, sharedMipOffset) && levels[0].Length != count)
                || sharedMipOffset >= count)))
            throw new ArgumentException("Texture upload requires complete nonempty mip tails.", nameof(levels));
        ulong bytes = 0;
        foreach (var layer in levels) {
            if (layer.Length != levels[0].Length)
                throw new ArgumentException("Array mip counts differ.", nameof(levels));
            for (var mip = 0; mip < count; mip++) {
                var expected = checked((ulong)System.Math.Max(1u, width >> mip) * System.Math.Max(1u, height >> mip) * 4);
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
            descriptor.Format = srgb ? WGPUTextureFormat.RGBA8UnormSrgb : WGPUTextureFormat.RGBA8Unorm;
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
        while (_levels.Length != 0 && budget - uploaded >= 4) {
            if (_levels[_layer][_mip].IsEmpty) { NextMip(); continue; }
            var width = System.Math.Max(1u, _width >> _mip);
            var height = System.Math.Max(1u, _height >> _mip);
            var x = (uint)_pixel % width;
            var y = (uint)_pixel / width;
            var pixels = (budget - uploaded) / 4;
            var rows = x == 0 && pixels >= width ? System.Math.Min(height - y, pixels / width) : 1u;
            var columns = rows > 1 ? width : System.Math.Min(width - x, pixels);
            var bytes = columns * rows * 4;
            var destination = new WGPUTexelCopyTextureInfo {
                Texture = (WGPUTexture*)Texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                MipLevel = (uint)_mip,
                Origin = new() { X = x, Y = y, Z = (uint)_layer },
                Aspect = WGPUTextureAspect.All
            };
            var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = columns * 4, RowsPerImage = rows };
            var size = new WGPUExtent3D { Width = columns, Height = rows, DepthOrArrayLayers = 1 };
            fixed (byte* data = _levels[_layer][_mip].Span) {
                WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)_gpu.Queue.DangerousGetHandle(), &destination,
                    data + (_pixel * 4), bytes, &layout, &size);
            }
            uploaded += bytes;
            _pixel += (int)(columns * rows);
            if (_pixel == width * height) {
                NextMip();
            }
        }
        return uploaded;
    }

    private void NextMip()
    {
        _pixel = 0;
        if (++_mip == _levels[_layer].Length) { _mip = 0; _layer++; }
        if (_layer == _levels.Length) _levels = [];
    }

    private uint CopyShared(uint budget)
    {
        if (_copyMip == _sharedMipCount || budget < 4) return 0;
        var encoder = Wgpu.CreateCommandEncoder(_gpu.Device);
        uint copied = 0;
        try {
            while (_copyMip < _sharedMipCount && budget - copied >= 4) {
                var mip = _targetMipOffset + _copyMip;
                var width = System.Math.Max(1u, _width >> mip);
                var height = System.Math.Max(1u, _height >> mip);
                var x = (uint)_copyPixel % width; var y = (uint)_copyPixel / width;
                var pixels = (budget - copied) / 4;
                var rows = x == 0 && pixels >= width ? System.Math.Min(height - y, pixels / width) : 1u;
                var columns = rows > 1 ? width : System.Math.Min(width - x, pixels);
                var source = new WGPUTexelCopyTextureInfo {
                    Texture = (WGPUTexture*)_previous.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                    MipLevel = (uint)(_sourceMipOffset + _copyMip),
                    Origin = new() { X = x, Y = y, Z = (uint)_copyLayer }, Aspect = WGPUTextureAspect.All
                };
                var destination = new WGPUTexelCopyTextureInfo {
                    Texture = (WGPUTexture*)Texture.GetWgpu<WGPUTexture>().DangerousGetHandle(), MipLevel = (uint)mip,
                    Origin = source.Origin, Aspect = WGPUTextureAspect.All
                };
                var extent = new WGPUExtent3D { Width = columns, Height = rows, DepthOrArrayLayers = 1 };
                WgpuUnsafe.wgpuCommandEncoderCopyTextureToTexture((WGPUCommandEncoder*)encoder.DangerousGetHandle(), &source, &destination, &extent);
                copied += columns * rows * 4; _copyPixel += (int)(columns * rows);
                if (_copyPixel == width * height) {
                    _copyPixel = 0;
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
