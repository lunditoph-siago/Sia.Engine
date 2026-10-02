using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public sealed unsafe class TextureArrayUpload : IDisposable
{
    private readonly GpuResources _gpu;
    private ReadOnlyMemory<byte>[][] _levels;
    private readonly uint _width, _height;
    private int _layer, _mip, _pixel;
    private bool _aborted;

    public Entity Texture { get; }
    public Entity View { get; }

    public ulong Bytes => _gpu.Bytes;

    public bool Complete => _levels.Length == 0 && !_aborted;

    public TextureArrayUpload(in GpuFrame frame, uint width, uint height, bool srgb,
        ReadOnlyMemory<byte>[][] levels, ulong budget)
    {
        if (width == 0 || height == 0 || levels.Length == 0 || levels[0].Length is < 1 or > 14
            || (levels[0].Length > 1 && (width >> (levels[0].Length - 2)) <= 1 && (height >> (levels[0].Length - 2)) <= 1))
            throw new ArgumentException("Texture upload requires complete nonempty mip tails.", nameof(levels));
        ulong bytes = 0;
        foreach (var layer in levels) {
            if (layer.Length != levels[0].Length)
                throw new ArgumentException("Array mip counts differ.", nameof(levels));
            for (var mip = 0; mip < layer.Length; mip++) {
                var expected = checked((ulong)System.Math.Max(1u, width >> mip) * System.Math.Max(1u, height >> mip) * 4);
                if ((ulong)layer[mip].Length != expected)
                    throw new ArgumentException("Texture mip byte count mismatch.", nameof(levels));
                bytes = checked(bytes + expected);
            }
        }
        (_width, _height, _levels) = (width, height, levels);
        _gpu = new(frame, budget);
        try {
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
            descriptor.MipLevelCount = (uint)levels[0].Length;
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
        uint uploaded = 0;
        while (!Complete && budget - uploaded >= 4) {
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
                _pixel = 0;
                _mip++;
                if (_mip == _levels[_layer].Length) {
                    _mip = 0;
                    _layer++;
                }
                if (_layer == _levels.Length) _levels = [];
            }
        }
        return uploaded;
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
