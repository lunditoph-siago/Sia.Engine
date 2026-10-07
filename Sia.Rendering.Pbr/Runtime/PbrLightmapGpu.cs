using Sia;
using Sia.WebGPU;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe partial class PbrLightmapGpu : IDisposable
{
    private readonly GpuResources _gpu;
    public Entity Texture { get; }
    public Entity View { get; }
    public ulong Bytes => _gpu.Bytes;

    public PbrLightmapGpu(in GpuFrame frame, PbrLightmapAsset asset, ulong maximumBytes)
    {
        _gpu = new(frame, maximumBytes);
        try {
            if (asset.Resolution > _gpu.Limits.MaxTextureDimension2D || _gpu.Limits.MaxTextureArrayLayers < 4)
                throw new NotSupportedException("Lightmap atlas exceeds device texture limits.");
            var descriptor = WGPUTextureDescriptor.Default;
            descriptor.Dimension = WGPUTextureDimension._2D;
            descriptor.Size = new() { Width = (uint)asset.Resolution, Height = (uint)asset.Resolution, DepthOrArrayLayers = 4 };
            var quantized = asset.Encoding == PbrLightmapEncoding.L1Unorm8;
            descriptor.Format = quantized ? WGPUTextureFormat.RGBA8Unorm : WGPUTextureFormat.RGBA16Float;
            descriptor.MipLevelCount = (uint)asset.MipCount;
            descriptor.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
            Texture = _gpu.Texture(descriptor, asset.TextureBytes);
            var view = WGPUTextureViewDescriptor.Default;
            view.Dimension = WGPUTextureViewDimension._2DArray;
            View = _gpu.Own(Wgpu.CreateTextureView(Texture.GetWgpu<WGPUTexture>(), view));
            var target = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)Texture.GetWgpu<WGPUTexture>().DangerousGetHandle(), Aspect = WGPUTextureAspect.All };
            var channelBytes = quantized ? 1 : 2;
            var rowBytes = asset.Resolution * 4 * channelBytes;
            // Layer-major uploads use bounded row scratch, not a second full atlas.
            var scratch = ArrayPool<byte>.Shared.Rent(rowBytes * System.Math.Min(16, asset.Resolution));
            try {
                for (var mip = 0; mip < asset.MipCount; mip++) {
                    var size = asset.Resolution >> mip;
                    rowBytes = size * 4 * channelBytes;
                    target.MipLevel = (uint)mip;
                    for (var band = 0; band < 4; band++)
                    for (var y = 0; y < size; y += 16) {
                        var rows = System.Math.Min(16, size - y);
                        asset.WriteMipRows(scratch, mip, band, y, rows);
                        target.Origin = new() { Y = (uint)y, Z = (uint)band };
                        var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = (uint)rowBytes, RowsPerImage = (uint)rows };
                        fixed (byte* data = scratch) {
                            var extent = new WGPUExtent3D { Width = (uint)size, Height = (uint)rows, DepthOrArrayLayers = 1 };
                            WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)_gpu.Queue.DangerousGetHandle(), &target,
                                data, (nuint)(rowBytes * rows), &layout, &extent);
                        }
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(scratch); }
        }
        catch { _gpu.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (!IsStopped) throw new InvalidOperationException("Await lightmap streaming shutdown before disposal.");
        _gpu.Dispose();
    }
}
