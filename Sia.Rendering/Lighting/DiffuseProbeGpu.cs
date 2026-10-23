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
    private SceneTraceData? _tracing;
    private readonly DiffuseProbeReference? _reference;
    private readonly Half[]? _clearTexture;
    private readonly IblEnvironmentGpu? _environment;
    private readonly double _referenceBound;
    private double _diffuseBound, _emissionBound;
    private float _radianceScale;
    private ProceduralSky? _lastSky;
    private float3 _lastTowardLight, _lastRadiance;
    private bool _correctionInvalidated = true;

    public Entity Buffer { get; }
    public Entity Texture { get; }
    public Entity TextureView { get; }
    public Entity Header { get; }
    public Entity Tracing { get; }
    public Entity TracingGeometry { get; }
    public Entity TracingInstances { get; }
    public Entity ReferenceConfiguration { get; }
    public Entity ReferenceSurfaces { get; }
    public Entity ReferenceInstances { get; }
    public uint? ReferenceSamples => _reference?.Samples;
    public bool SignedCorrection => _reference is not null;
    private uint TextureBands => k_TextureBands * (SignedCorrection ? 2u : 1u);

    public Entity Configuration => _config;

    public ulong Bytes => _gpu.Bytes;

    public bool Dynamic => _pipeline.IsValid;

    public int Count => _shape.Count;

    internal static string ShaderSource(World world)
        => CompileProbe(world, false);

    internal static string ShaderSourceInstanced(World world)
        => CompileProbe(world, true);

    internal static string ShaderSourcePaired(World world) => CompileProbe(world, true, true);

    private static string CompileProbe(World world, bool instanced, bool paired = false, bool environment = false, bool referenceEnvironment = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        string? Read(string module) => RenderingShaderSource.AcquireModule(world, module) is { } entity
            ? entity.Get<WgslSourceResource>().Source : null;
        var defines = new Dictionary<string, string>();
        if (instanced) { defines["INSTANCE_PROBE_TRACE"] = "1"; defines["INSTANCE_TRACE_FILTER"] = "1"; }
        if (paired) { defines["PAIRED_PROBE_TRACE"] = "1"; defines["INSTANCE_TRACE_REFERENCE"] = "1"; }
        if (environment) defines["PROBE_ENVIRONMENT"] = "1";
        if (referenceEnvironment) defines["PROBE_REFERENCE_ENVIRONMENT"] = "1";
        return RenderingShaderSource.CompileResolved(Read("rendering/Baking/probes")
            ?? throw new InvalidOperationException("Missing probe bake source."), (module, _) => Read(module),
            defines);
    }

    public static ulong FieldBytes(uint3 dimensions, bool dynamic)
    {
        var count = checked((ulong)dimensions.x * dimensions.y * dimensions.z);
        if (dimensions.x < 2 || dimensions.y < 2 || dimensions.z < 2
            || count > DiffuseProbeAsset.MaximumProbes)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        return checked((3 + count * 9) * 16 + 48 + count * k_TextureBands * 8
            + (dynamic ? 176ul : 0));
    }

    /// <summary>Total GPU bytes, counting shared local geometry once. Excludes immutable borrowed input and host reset scratch.</summary>
    public static ulong RequiredBytes(uint3 dimensions, SceneTraceData? tracing, DiffuseProbeReference? reference = null)
    {
        if (reference is not null) reference.Validate(tracing ?? throw new ArgumentException("A correction requires live transport."));
        return checked(FieldBytes(dimensions, tracing is not null) + (tracing?.PayloadBytes ?? 0)
            + (reference is null ? 0 : ((ulong)reference.Scene.Instances.Length + (ulong)reference.Scene.Packed.Length) * 16 + 176
                + (ulong)dimensions.x * dimensions.y * dimensions.z * k_TextureBands * 8));
    }

    public DiffuseProbeGpu(in GpuFrame frame, DiffuseProbeAsset asset, SceneTraceData? tracing,
        ulong maximumBytes, DiffuseProbeReference? reference)
        : this(frame, asset, tracing, maximumBytes, reference, null, null) { }

    /// <param name="environment">Optional borrowed live miss source; its owner must outlive this field.</param>
    public DiffuseProbeGpu(in GpuFrame frame, DiffuseProbeAsset asset,
        SceneTraceData? tracing = null, ulong maximumBytes = 160ul * 1024 * 1024,
        DiffuseProbeReference? reference = null, IblEnvironmentGpu? environment = null)
        : this(frame, asset, tracing, maximumBytes, reference, environment, null) { }

    /// <param name="referenceEnvironment">Borrowed frozen cube for paired transport; retain its owner and do not update it.</param>
    public DiffuseProbeGpu(in GpuFrame frame, DiffuseProbeAsset asset, SceneTraceData? tracing,
        ulong maximumBytes, DiffuseProbeReference? reference, IblEnvironmentGpu? environment,
        IblEnvironmentGpu? referenceEnvironment)
    {
        if (referenceEnvironment is not null && reference is null)
            throw new ArgumentException("A frozen HDR environment requires paired reference transport.", nameof(referenceEnvironment));
        if (RequiredBytes(asset.Dimensions, tracing, reference) > maximumBytes)
            throw new ArgumentException("Probe field and complete transport exceed the GPU budget.", nameof(maximumBytes));
        _gpu = new(frame, maximumBytes);
        _frame = frame;
        _shape = asset;
        _reference = reference;
        _environment = environment;
        _radianceScale = TextureScale(asset);
        try {
            if (tracing is not null) (_diffuseBound, _emissionBound) = SurfaceBounds(tracing);
            if (reference is not null) {
                var bounds = SurfaceBounds(reference.Scene);
                _referenceBound = IncidentBound(referenceEnvironment?.MaximumRadiance ?? SkyBound(reference.Sky), bounds.Emission, bounds.Diffuse, reference.LightRadiance);
            }
            if (asset.Dimensions.x > _gpu.Limits.MaxTextureDimension3D
                || asset.Dimensions.y > _gpu.Limits.MaxTextureDimension3D
                || asset.Dimensions.z > _gpu.Limits.MaxTextureDimension3D / TextureBands)
                throw new NotSupportedException("Probe coefficient texture exceeds the device's 3D texture limit.");
            var packed = asset.Packed();
            if (SignedCorrection) { Array.Clear(packed, 3, packed.Length - 3); packed[2].z = 1; }
            packed[2].w = _radianceScale == 1 ? 0 : _radianceScale;
            Buffer = _gpu.Upload<float4>(packed, WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst);
            Header = _gpu.Upload<float4>(packed.AsSpan(0, 3), WGPUBufferUsage.Uniform);
            var texture = WGPUTextureDescriptor.Default;
            texture.Dimension = WGPUTextureDimension._3D;
            texture.Size = new() {
                Width = asset.Dimensions.x,
                Height = asset.Dimensions.y,
                DepthOrArrayLayers = asset.Dimensions.z * TextureBands
            };
            texture.Format = WGPUTextureFormat.RGBA16Float;
            texture.Usage = WGPUTextureUsage.StorageBinding | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst;
            Texture = _gpu.Texture(texture, (ulong)asset.Count * TextureBands * 8);
            var view = WGPUTextureViewDescriptor.Default;
            view.Dimension = WGPUTextureViewDimension._3D;
            TextureView = _gpu.Own(Wgpu.CreateTextureView(Texture.GetWgpu<WGPUTexture>(), view));
            if (tracing is not null) _clearTexture = new Half[asset.Count * (int)TextureBands * 4];
            if (SignedCorrection) UploadTexture(_clearTexture!);
            else UploadTexture(TextureData(asset));
            if (tracing is null) return;
            if (_gpu.Limits.MaxComputeInvocationsPerWorkgroup < 64 || _gpu.Limits.MaxComputeWorkgroupStorageSize < 9216)
                throw new NotSupportedException("Probe integration requires 64 compute lanes and 9216 bytes of workgroup storage.");
            if (!asset.SceneIdentity.Span.SequenceEqual(tracing.Identity.Span))
                throw new ArgumentException("Probe volume and trace scene identities disagree.");
            Tracing = _gpu.Upload<float4>(tracing.Packed.Span, WGPUBufferUsage.Storage);
            if (tracing.IsInstanced) {
                TracingGeometry = _gpu.Upload<uint4>(tracing.Geometry.Span, WGPUBufferUsage.Storage);
                TracingInstances = _gpu.Upload<uint4>(tracing.Instances.Span, WGPUBufferUsage.Storage);
                _tracing = tracing;
            }
            _config = _gpu.Buffer(176, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
            var layoutEntries = new List<WGPUBindGroupLayoutEntry> {
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 176),
                GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(2, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.StorageTexture(3, WGPUTextureFormat.RGBA16Float, WGPUTextureViewDimension._3D)
            };
            var groupEntries = new List<WGPUBindGroupEntry> {
                GpuBinding.Buffer(0, _config), GpuBinding.Buffer(1, Tracing), GpuBinding.Buffer(2, Buffer),
                GpuBinding.Texture(3, TextureView.GetWgpu<WGPUTextureView>())
            };
            if (tracing.IsInstanced) {
                layoutEntries.Add(GpuBinding.Buffer(4, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute));
                layoutEntries.Add(GpuBinding.Buffer(5, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute));
                groupEntries.Add(GpuBinding.Buffer(4, TracingGeometry)); groupEntries.Add(GpuBinding.Buffer(5, TracingInstances));
            }
            if (reference is not null) {
                if (_gpu.Limits.MaxStorageBuffersPerShaderStage < 6)
                    throw new NotSupportedException("Paired transport requires six storage buffers.");
                ReferenceConfiguration = _gpu.Buffer(176, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
                ReferenceSurfaces = _gpu.Upload<float4>(reference.Scene.Packed.Span, WGPUBufferUsage.Storage);
                ReferenceInstances = _gpu.Upload<uint4>(reference.Scene.Instances.Span, WGPUBufferUsage.Storage);
                Span<float4> referenceValues = stackalloc float4[11];
                LightingValues(referenceValues, reference.Sky, reference.TowardLight, reference.LightRadiance,
                    reference.MaximumDistance, reference.RayBias, referenceEnvironment);
                Wgpu.WriteBuffer<float4>(_gpu.Queue, ReferenceConfiguration.GetWgpu<WGPUBuffer>(), 0, referenceValues);
                layoutEntries.Add(GpuBinding.Buffer(6, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 176));
                layoutEntries.Add(GpuBinding.Buffer(7, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute));
                layoutEntries.Add(GpuBinding.Buffer(8, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute));
                groupEntries.Add(GpuBinding.Buffer(6, ReferenceConfiguration));
                groupEntries.Add(GpuBinding.Buffer(7, ReferenceSurfaces)); groupEntries.Add(GpuBinding.Buffer(8, ReferenceInstances));
            }
            if (environment is not null) {
                layoutEntries.Add(GpuBinding.Texture(9, WGPUTextureSampleType.Float, WGPUShaderStage.Compute, WGPUTextureViewDimension.Cube));
                layoutEntries.Add(GpuBinding.Sampler(10, WGPUShaderStage.Compute));
                groupEntries.Add(GpuBinding.Texture(9, environment.CubeView.GetWgpu<WGPUTextureView>()));
                groupEntries.Add(GpuBinding.Sampler(10, environment.Sampler));
            }
            if (referenceEnvironment is not null) {
                layoutEntries.Add(GpuBinding.Texture(11, WGPUTextureSampleType.Float, WGPUShaderStage.Compute, WGPUTextureViewDimension.Cube));
                layoutEntries.Add(GpuBinding.Sampler(12, WGPUShaderStage.Compute));
                groupEntries.Add(GpuBinding.Texture(11, referenceEnvironment.CubeView.GetWgpu<WGPUTextureView>()));
                groupEntries.Add(GpuBinding.Sampler(12, referenceEnvironment.Sampler));
            }
            var layout = GpuBinding.Layout(_gpu, [.. layoutEntries]);
            _group = GpuBinding.Group(_gpu, layout, [.. groupEntries]);
            var pipelineLayout = GpuBinding.PipelineLayout(_gpu, layout);
            var source = CompileProbe(frame.ResourceWorld, tracing.IsInstanced, SignedCorrection, environment is not null, referenceEnvironment is not null);
            var shader = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device, source, "scene-probe-transport"));
            _pipeline = CreatePipeline(_gpu, pipelineLayout, shader);
        }
        catch { _gpu.Dispose(); throw; }
    }

    public void Prepare(ProceduralSky sky, float3 towardLight, float3 lightRadiance,
        uint updates, uint samples = 64, float maximumDistance = 1000)
    {
        if (!Dynamic) throw new InvalidOperationException("A baked probe field has no integration pipeline.");
        sky.ValidateData();
        if (_reference is { } reference) { samples = reference.Samples; maximumDistance = reference.MaximumDistance; }
        if (updates == 0 || updates > Count || samples is < 16 or > 1024
            || !float.IsFinite(maximumDistance) || maximumDistance <= 0
            || !float.IsFinite(math.lengthsq(towardLight)) || math.lengthsq(towardLight) < 1e-12f
            || !float.IsFinite(lightRadiance.x) || !float.IsFinite(lightRadiance.y) || !float.IsFinite(lightRadiance.z)
            || lightRadiance.x < 0 || lightRadiance.y < 0 || lightRadiance.z < 0)
            throw new ArgumentException("Invalid probe integration budget or lighting.");
        var bound = IncidentBound(_environment?.MaximumRadiance ?? SkyBound(sky), _emissionBound, _diffuseBound, lightRadiance);
        var peak = 8 * (bound + _referenceBound);
        _updates = 0;
        if (peak > 65504d * _radianceScale) {
            var scale = ScaleFor(peak);
            // One bounded reset preserves uniform physical units across mixed-age texels.
            UploadTexture(_clearTexture!);
            Wgpu.WriteBuffer<float>(_gpu.Queue, Header.GetWgpu<WGPUBuffer>(), 44, [scale]);
            Wgpu.WriteBuffer<float>(_gpu.Queue, Buffer.GetWgpu<WGPUBuffer>(), 44, [scale]);
            _radianceScale = scale;
            _correctionInvalidated = true;
        }
        if (SignedCorrection && (_lastSky is not null && (!_lastSky.Equals(sky)
            || !_lastTowardLight.Equals(towardLight) || !_lastRadiance.Equals(lightRadiance)))) InvalidateCorrection();
        Span<float4> values = stackalloc float4[11];
        LightingValues(values, sky, towardLight, lightRadiance, maximumDistance, _reference?.RayBias ?? .01f, _environment);
        values[10] = new(_cursor, updates, samples, 0);
        Wgpu.WriteBuffer<float4>(_gpu.Queue, _config.GetWgpu<WGPUBuffer>(), 0, values);
        _cursor = (_cursor + updates) % (uint)Count;
        _updates = updates;
        _lastSky = sky; _lastTowardLight = towardLight; _lastRadiance = lightRadiance;
    }

    private void LightingValues(Span<float4> values, ProceduralSky sky, float3 towardLight, float3 lightRadiance,
        float maximumDistance, float bias, IblEnvironmentGpu? environment)
    {
        values[0] = new(sky.Horizon, sky.Intensity); values[1] = new(sky.Zenith, sky.SunExponent); values[2] = new(sky.Ground, 0);
        values[3] = new(math.normalize(sky.SunDirection), 0); values[4] = new(sky.SunRadiance, 0);
        values[5] = new(_shape.Origin, _shape.Dimensions.x); values[6] = new(_shape.Step, _shape.Dimensions.y);
        values[7] = new(_shape.Dimensions.z, Count, environment?.CubeRadianceScale ?? 0, _radianceScale);
        values[8] = new(math.normalize(towardLight), maximumDistance);
        values[9] = new(lightRadiance, bias); values[10] = default;
    }

    private void InvalidateCorrection()
    {
        if (!SignedCorrection || _clearTexture is null || _correctionInvalidated) return;
        UploadTexture(_clearTexture); _correctionInvalidated = true;
    }

    /// <summary>Uploads changed live instance/refit and material rows; geometry and frozen reference buffers remain shared/unchanged.</summary>
    public void UpdateInstances(SceneTraceData tracing)
    {
        ArgumentNullException.ThrowIfNull(tracing);
        if (_tracing is not { } current || !tracing.IsInstanced || !tracing.Geometry.Equals(current.Geometry)
            || !tracing.Identity.Equals(current.Identity) || tracing.Packed.Length != current.Packed.Length
            || tracing.Instances.Length != current.Instances.Length)
            throw new ArgumentException("Live probe updates must share validated geometry, material-table shape and authored identity.", nameof(tracing));
        var rows = tracing.Instances.Span; var old = current.Instances.Span;
        var changed = false;
        for (var first = 0; first < rows.Length;) {
            if (rows[first].Equals(old[first])) { first++; continue; }
            var end = first + 1;
            while (end < rows.Length && !rows[end].Equals(old[end])) end++;
            Wgpu.WriteBuffer<uint4>(_gpu.Queue, TracingInstances.GetWgpu<WGPUBuffer>(), (ulong)first * 16, rows.Slice(first, end - first));
            changed = true;
            first = end;
        }
        var surfaces = tracing.Packed.Span; var previousSurfaces = current.Packed.Span;
        for (var first = 0; first < surfaces.Length;) {
            if (surfaces[first].Equals(previousSurfaces[first])) { first++; continue; }
            var end = first + 1; while (end < surfaces.Length && !surfaces[end].Equals(previousSurfaces[end])) end++;
            Wgpu.WriteBuffer<float4>(_gpu.Queue, Tracing.GetWgpu<WGPUBuffer>(), (ulong)first * 16, surfaces.Slice(first, end - first));
            changed = true; first = end;
        }
        if (changed) { InvalidateCorrection(); (_diffuseBound, _emissionBound) = SurfaceBounds(tracing); }
        _tracing = tracing;
    }

    internal static Half[] TextureData(DiffuseProbeAsset asset)
    {
        var scale = TextureScale(asset);
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
                values[at] = (Half)(channel < 27 ? value / scale : value);
            }
        return values;
    }

    private static float TextureScale(DiffuseProbeAsset asset)
    {
        var peak = 0d;
        foreach (var c in asset.Coefficients.Span)
            peak = System.Math.Max(peak, (double)math.cmax(math.abs(c.xyz)) * c.w);
        return ScaleFor(peak);
    }

    private static float ScaleFor(double peak)
        => peak <= 65504 ? 1 : MathF.ScaleB(1, (int)System.Math.Ceiling(System.Math.Log2(peak / 65504)));

    private static (double Diffuse, double Emission) SurfaceBounds(SceneTraceData scene)
    {
        var rows = scene.Packed.Span;
        double diffuse = 0, emission = 0;
        for (var at = scene.IsInstanced ? 0 : (int)rows[0].w; at < rows.Length; at += 2) {
            diffuse = System.Math.Max(diffuse, math.cmax(rows[at].xyz));
            emission = System.Math.Max(emission, math.cmax(rows[at + 1].xyz));
        }
        return (diffuse, emission);
    }

    private static double SkyBound(ProceduralSky sky)
        => ((double)math.cmax(math.max(sky.Horizon, math.max(sky.Zenith, sky.Ground))) + math.cmax(sky.SunRadiance)) * sky.Intensity;

    private static double IncidentBound(double sky, double emission, double diffuse, float3 light)
    {
        var bound = System.Math.Max(sky, emission + diffuse * math.cmax(light) / System.Math.PI);
        // Reserve headroom for all 1024 samples, SH products and paired subtraction before reduction.
        if (!double.IsFinite(bound) || bound > float.MaxValue / 4096d)
            throw new ArgumentException("Probe incident radiance exceeds finite Float32 integration headroom.");
        return bound;
    }

    private unsafe void UploadTexture(Half[] values)
    {
        var target = new WGPUTexelCopyTextureInfo { Texture = (WGPUTexture*)Texture.GetWgpu<WGPUTexture>().DangerousGetHandle(), Aspect = WGPUTextureAspect.All };
        var layout = new WGPUTexelCopyBufferLayout { BytesPerRow = _shape.Dimensions.x * 8, RowsPerImage = _shape.Dimensions.y };
        var extent = new WGPUExtent3D { Width = _shape.Dimensions.x, Height = _shape.Dimensions.y, DepthOrArrayLayers = _shape.Dimensions.z * TextureBands };
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
        _correctionInvalidated = false;
    }

    public DiffuseProbeAsset Bake(WgpuHandle<WGPUInstance> instance, ProceduralSky sky,
        float3 towardLight, float3 lightRadiance, uint samples = 256)
    {
        if (SignedCorrection) throw new InvalidOperationException("Runtime signed correction cannot be persisted as an absolute baked probe asset.");
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

    public void Dispose() { _tracing = null; _gpu.Dispose(); }
}
