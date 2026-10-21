using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

// Renderer-owned immutable capture upload. Regional metadata is separate from shared environment content.
internal sealed unsafe class PbrReflectionCaptureGpu : IDisposable
{
    internal const int MaximumRegions = 16;
    internal const int UniformBytes = 16 + MaximumRegions * 48;
    private const ulong CubeBytes = IblEnvironmentAsset.CubeTexels * 8ul;
    private readonly GpuResources _gpu;
    internal Entity Texture { get; }
    internal Entity View { get; }
    internal Entity Regions { get; }
    internal ulong Bytes => _gpu.Bytes;

    internal static PbrReflectionCaptureInput[] Validate(ReadOnlySpan<PbrReflectionCaptureInput> inputs,
        ReadOnlySpan<byte> renderIdentity, IblEnvironmentAsset? source, ulong maximumBytes)
    {
        if (inputs.IsEmpty) return [];
        if (inputs.Length > MaximumRegions || renderIdentity.Length != 32 || source is null)
            throw new ArgumentException("Capture rendering requires bounded regions, canonical scene identity and the exact baked source environment.");
        var sourceHash = source.Write(Stream.Null);
        var ids = new HashSet<Sia.Asset.AssetId>(); var content = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs) {
            ArgumentNullException.ThrowIfNull(input);
            if (!input.RenderIdentity.Span.SequenceEqual(renderIdentity) || input.Capture.SourceEnvironment.ContentHash != sourceHash
                || !ids.Add(input.Capture.ProductId))
                throw new ArgumentException("Capture inputs differ from current render geometry/materials/environment or duplicate a region ID.");
            content.Add(input.Capture.Environment.ContentHash);
        }
        if (CubeBytes * (ulong)content.Count + UniformBytes > maximumBytes)
            throw new ArgumentException("Capture texture and metadata exceed their GPU admission.", nameof(maximumBytes));
        var sorted = inputs.ToArray(); Array.Sort(sorted, static (a, b) => a.Capture.ProductId.Value.CompareTo(b.Capture.ProductId.Value));
        return sorted;
    }

    internal PbrReflectionCaptureGpu(in GpuFrame frame, ReadOnlySpan<PbrReflectionCaptureInput> inputs, ulong maximumBytes)
    {
        var layers = new Dictionary<string, int>(StringComparer.Ordinal); var environments = new List<IblEnvironmentAsset>();
        var rows = new float4[UniformBytes / 16]; rows[0].x = inputs.Length;
        for (var at = 0; at < inputs.Length; at++) {
            var input = inputs[at]; var data = input.Capture;
            if (!layers.TryGetValue(data.Environment.ContentHash, out var layer)) {
                layer = layers.Count; layers.Add(data.Environment.ContentHash, layer); environments.Add(input.Environment);
            }
            rows[1 + at * 3] = new(data.Position, layer * 6);
            rows[2 + at * 3] = new(data.Bounds.Min, 0); rows[3 + at * 3] = new(data.Bounds.Max, 0);
        }
        _gpu = new(frame, maximumBytes);
        try {
            if (inputs.Length is < 1 or > MaximumRegions || environments.Count * 6 > _gpu.Limits.MaxTextureArrayLayers
                || UniformBytes > _gpu.Limits.MaxUniformBufferBindingSize)
                throw new NotSupportedException("Capture region/texture layout exceeds device admission.");
            var descriptor = WGPUTextureDescriptor.Default;
            descriptor.Dimension = WGPUTextureDimension._2D; descriptor.Format = WGPUTextureFormat.RGBA16Float;
            descriptor.Size = new() { Width = IblEnvironmentAsset.CubeSize, Height = IblEnvironmentAsset.CubeSize, DepthOrArrayLayers = (uint)environments.Count * 6 };
            descriptor.MipLevelCount = IblEnvironmentAsset.MipCount;
            descriptor.Usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
            var dimension = WGPUTextureBindingViewDimension.Default;
            dimension.TextureBindingViewDimension = WGPUTextureViewDimension._2DArray;
            if (OperatingSystem.IsBrowser()) descriptor.NextInChain = &dimension.Chain;
            Texture = _gpu.Texture(descriptor, (ulong)environments.Count * CubeBytes);
            var view = WGPUTextureViewDescriptor.Default; view.Dimension = WGPUTextureViewDimension._2DArray;
            View = _gpu.Own(Wgpu.CreateTextureView(Texture.GetWgpu<WGPUTexture>(), view));
            Regions = _gpu.Buffer(UniformBytes, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
            Wgpu.WriteBuffer<float4>(_gpu.Queue, Regions.GetWgpu<WGPUBuffer>(), 0, rows);
            for (var layer = 0; layer < environments.Count; layer++) {
                var offset = 0;
                for (var mip = 0; mip < IblEnvironmentAsset.MipCount; mip++) {
                    var edge = IblEnvironmentAsset.CubeSize >> mip; var length = edge * edge * 4;
                    for (var face = 0; face < 6; face++) {
                        var target = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)Texture.GetWgpu<WGPUTexture>().DangerousGetHandle(),
                            MipLevel = (uint)mip, Origin = new() { Z = (uint)(layer * 6 + face) }, Aspect = WGPUTextureAspect.All };
                        var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = (uint)edge * 8, RowsPerImage = (uint)edge };
                        var extent = new WGPUExtent3D { Width = (uint)edge, Height = (uint)edge, DepthOrArrayLayers = 1 };
                        var values = environments[layer].Cube.Span.Slice(offset, length);
                        fixed (Half* data = values)
                            WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)_gpu.Queue.DangerousGetHandle(), &target, data,
                                (nuint)values.Length * 2, &layout, &extent);
                        offset += length;
                    }
                }
            }
        }
        catch { _gpu.Dispose(); throw; }
    }

    public void Dispose() => _gpu.Dispose();
}
