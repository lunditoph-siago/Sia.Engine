using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering;

public sealed class DiffuseProbeGpu : IDisposable
{
    internal const uint k_TextureBands = 7;

    private readonly GpuResources _gpu;
    private readonly GpuFrame _frame;
    private readonly DiffuseProbeAsset _shape;
    private readonly Entity _config;
    private readonly Entity _group;
    private readonly Entity _pipeline;
    private uint _cursor;
    private uint _updates;

    public Entity Buffer { get; }
    public Entity Texture { get; }
    public Entity TextureView { get; }
    public Entity Header { get; }
    public Entity Tracing { get; }

    public Entity Configuration => _config;

    public ulong Bytes => _gpu.Bytes;

    public bool Dynamic => _pipeline.IsValid;

    public int Count => _shape.Count;

    public static ulong FieldBytes(uint3 dimensions, bool dynamic)
    {
        var count = checked((ulong)dimensions.x * dimensions.y * dimensions.z);
        if (dimensions.x < 2 || dimensions.y < 2 || dimensions.z < 2
            || count > DiffuseProbeAsset.MaximumProbes)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        return checked((3 + count * 9) * 16 + 48 + count * k_TextureBands * 8
            + (dynamic ? 176ul : 0));
    }

    public DiffuseProbeGpu(in GpuFrame frame, DiffuseProbeAsset asset,
        SceneTraceData? tracing = null, ulong maximumBytes = 160ul * 1024 * 1024)
    {
        _gpu = new(frame, maximumBytes);
        _frame = frame;
        _shape = asset;
        try {
            if (asset.Dimensions.x > _gpu.Limits.MaxTextureDimension3D
                || asset.Dimensions.y > _gpu.Limits.MaxTextureDimension3D
                || asset.Dimensions.z > _gpu.Limits.MaxTextureDimension3D / k_TextureBands)
                throw new NotSupportedException("Probe coefficient texture exceeds the device's 3D texture limit.");
            var packed = asset.Packed();
            Buffer = _gpu.Upload<float4>(packed, WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
            Header = _gpu.Upload<float4>(packed.AsSpan(0, 3), WGPUBufferUsage.Uniform);
            var texture = WGPUTextureDescriptor.Default;
            texture.Dimension = WGPUTextureDimension._3D;
            texture.Size = new() {
                Width = asset.Dimensions.x,
                Height = asset.Dimensions.y,
                DepthOrArrayLayers = asset.Dimensions.z * k_TextureBands
            };
            texture.Format = WGPUTextureFormat.RGBA16Float;
            texture.Usage = WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
            Texture = _gpu.Texture(texture, (ulong)asset.Count * k_TextureBands * 8);
            var view = WGPUTextureViewDescriptor.Default;
            view.Dimension = WGPUTextureViewDimension._3D;
            TextureView = _gpu.Own(Wgpu.CreateTextureView(Texture.GetWgpu<WGPUTexture>(), view));
            UploadTexture(asset);
            if (tracing is null) return;
            if (_gpu.Limits.MaxComputeInvocationsPerWorkgroup < 64 || _gpu.Limits.MaxComputeWorkgroupStorageSize < 9216)
                throw new NotSupportedException("Probe integration requires 64 compute lanes and 9216 bytes of workgroup storage.");
            if (!asset.SceneIdentity.Span.SequenceEqual(tracing.Identity.Span))
                throw new ArgumentException("Probe volume and trace scene identities disagree.");
            Tracing = _gpu.Upload<float4>(tracing.Packed.Span, WGPUBufferUsage.Storage);
            _config = _gpu.Buffer(176, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
            var layout = GpuBinding.Layout(_gpu, [
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 176),
                GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(2, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.StorageTexture(3, WGPUTextureFormat.RGBA16Float, WGPUTextureViewDimension._3D)
            ]);
            _group = GpuBinding.Group(_gpu, layout, [
                GpuBinding.Buffer(0, _config), GpuBinding.Buffer(1, Tracing), GpuBinding.Buffer(2, Buffer),
                GpuBinding.Texture(3, TextureView.GetWgpu<WGPUTextureView>())
            ]);
            var pipelineLayout = GpuBinding.PipelineLayout(_gpu, layout);
            var source = RenderingShaderSource.Compile(RenderingShaderSource.ReadModule("rendering/Baking/probes")!);
            var shader = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device, source, "scene-probe-transport"));
            _pipeline = CreatePipeline(_gpu, pipelineLayout, shader);
        }
        catch { _gpu.Dispose(); throw; }
    }

    public void Prepare(ProceduralSky sky, float3 towardLight, float3 lightRadiance,
        uint updates, uint samples = 64, float maximumDistance = 1000)
    {
        if (!Dynamic) throw new InvalidOperationException("A baked probe field has no integration pipeline.");
        sky.Validate();
        if (updates == 0 || updates > Count || samples is < 16 or > 1024
            || !float.IsFinite(maximumDistance) || maximumDistance <= 0
            || !float.IsFinite(math.lengthsq(towardLight)) || math.lengthsq(towardLight) < 1e-12f
            || !float.IsFinite(lightRadiance.x) || !float.IsFinite(lightRadiance.y) || !float.IsFinite(lightRadiance.z)
            || lightRadiance.x < 0 || lightRadiance.y < 0 || lightRadiance.z < 0)
            throw new ArgumentException("Invalid probe integration budget or lighting.");
        var values = new float4[] {
            new(sky.Horizon, sky.Intensity), new(sky.Zenith, sky.SunExponent), new(sky.Ground, 0),
            new(math.normalize(sky.SunDirection), 0), new(sky.SunRadiance, 0),
            new(_shape.Origin, _shape.Dimensions.x), new(_shape.Step, _shape.Dimensions.y),
            new(_shape.Dimensions.z, Count, 0, 0), new(math.normalize(towardLight), maximumDistance),
            new(lightRadiance, 0), new(_cursor, updates, samples, 0)
        };
        Wgpu.WriteBuffer<float4>(_gpu.Queue, _config.GetWgpu<WGPUBuffer>(), 0, values);
        _cursor = (_cursor + updates) % (uint)Count;
        _updates = updates;
    }

    internal static Half[] TextureData(DiffuseProbeAsset asset)
    {
        var values = new Half[asset.Count * k_TextureBands * 4];
        for (var channel = 0; channel < 28; channel++)
            for (var probe = 0; probe < asset.Count; probe++) {
                var validity = asset.Coefficients.Span[(probe * 9) + 8].w;
                var value = validity;
                if (channel < 27) {
                    var c = asset.Coefficients.Span[(probe * 9) + (channel / 3)];
                    value = ((channel % 3) switch {
                        0 => c.x,
                        1 => c.y,
                        _ => c.z
                    }) * c.w;
                }
                var at = (((channel / 4 * asset.Count) + probe) * 4) + (channel % 4);
                values[at] = (Half)System.Math.Clamp(value, -65504, 65504);
            }
        return values;
    }

    private unsafe void UploadTexture(DiffuseProbeAsset asset)
    {
        var values = TextureData(asset);
        var target = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)Texture.GetWgpu<WGPUTexture>().DangerousGetHandle(), Aspect = WGPUTextureAspect.All };
        var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = asset.Dimensions.x * 8, RowsPerImage = asset.Dimensions.y };
        var extent = new WGPUExtent3D { Width = asset.Dimensions.x, Height = asset.Dimensions.y, DepthOrArrayLayers = asset.Dimensions.z * k_TextureBands };
        fixed (Half* data = values)
            WgpuUnsafe.wgpuQueueWriteTexture((WGPUQueue*)_gpu.Queue.DangerousGetHandle(), &target, data, (nuint)values.Length * 2, &layout, &extent);
    }

    public void Integrate(WgpuHandle<WGPUCommandEncoder> encoder)
    {
        if (!Dynamic || _updates == 0)
            throw new InvalidOperationException("Prepare the dynamic field before integration.");
        var pass = Wgpu.BeginComputePass(encoder, WGPUComputePassDescriptor.Default);
        try {
            Wgpu.SetBindGroup(pass, 0, _group.GetWgpu<WGPUBindGroup>());
            Wgpu.SetComputePipeline(pass, _pipeline.GetWgpu<WGPUComputePipeline>());
            Wgpu.DispatchWorkgroups(pass, _updates);
        }
        finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
    }

    public DiffuseProbeAsset Bake(WgpuHandle<WGPUInstance> instance, ProceduralSky sky,
        float3 towardLight, float3 lightRadiance, uint samples = 256)
    {
        Prepare(sky, towardLight, lightRadiance, (uint)Count, samples);
        var bytes = (ulong)(3 + (Count * 9)) * 16;
        using var temporary = new GpuResources(_frame, bytes);
        var readback = temporary.Buffer(bytes, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
        var encoder = temporary.Own(Wgpu.CreateCommandEncoder(temporary.Device));
        Integrate(encoder.GetWgpu<WGPUCommandEncoder>());
        Wgpu.CopyBufferToBuffer(encoder.GetWgpu<WGPUCommandEncoder>(), Buffer.GetWgpu<WGPUBuffer>(), 0,
            readback.GetWgpu<WGPUBuffer>(), 0, bytes);
        var commands = temporary.Own(Wgpu.FinishCommandEncoder(encoder.GetWgpu<WGPUCommandEncoder>(), WGPUCommandBufferDescriptor.Default));
        Wgpu.Submit(temporary.Queue, [commands.GetWgpu<WGPUCommandBuffer>()]);
        Wgpu.MapBufferRead(instance, readback.GetWgpu<WGPUBuffer>(), 0, bytes, TimeSpan.FromSeconds(30));
        try {
            var values = Wgpu.GetMappedRangeReadOnly<float4>(readback.GetWgpu<WGPUBuffer>(), 0, 3 + (Count * 9));
            return new(_shape.Origin, _shape.Step, _shape.Dimensions, _shape.SceneIdentity.Span, values[3..]);
        }
        finally {
            Wgpu.UnmapBuffer(readback.GetWgpu<WGPUBuffer>());
        }
    }

    private static unsafe Entity CreatePipeline(GpuResources gpu, Entity layout, Entity shader)
    {
        var name = "integrate_probes"u8;
        fixed (byte* pointer = name) {
            var descriptor = WGPUComputePipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)layout.GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Compute.Module = (WGPUShaderModule*)shader.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            descriptor.Compute.EntryPoint = new() {
                Data = pointer,
                Length = (nuint)name.Length
            };
            return gpu.Own(Wgpu.CreateComputePipeline(gpu.Device, descriptor));
        }
    }

    public void Dispose() => _gpu.Dispose();
}
