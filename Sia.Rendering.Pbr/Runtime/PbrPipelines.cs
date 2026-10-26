using System.Text;
using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe class PbrPipelines : IDisposable
{
    private const WGPUShaderStage k_All = WGPUShaderStage.Compute | WGPUShaderStage.Vertex | WGPUShaderStage.Fragment;
    private const WGPUShaderStage k_Shade = WGPUShaderStage.Compute | WGPUShaderStage.Fragment;

    private readonly GpuResources _gpu;
    private readonly World _sourceWorld;
    private readonly bool _sceneGi;
    private readonly bool _staticLightmap;
    private readonly bool _residentInstances;
    private readonly bool _rasterPositionTables;
    private readonly bool _reflectionCaptures;
    private readonly bool _liveReflections;
    private readonly bool _cpuStreamInstances;

    public bool SeparateDirectResolvePass { get; }

    public Entity FrameLayout { get; }
    public Entity RasterFrameLayout { get; }
    public Entity ClusterLayout { get; }
    public Entity GeometryLayout { get; }
    public Entity RasterGeometryLayout { get; }
    public Entity MaterialLayout { get; }
    public Entity ResolveLayout { get; }
    public Entity TileLayout { get; }
    public Entity GlassLayout { get; }
    public Entity OutputLayout { get; }
    public Entity ExposureLayout { get; }
    public Entity ExposureMeasure { get; }
    public Entity ExposureReduce { get; }
    public Entity BackgroundLayout { get; }
    public Entity StreamWorkLayout { get; }
    public Entity ReflectionTraceLayout { get; }
    public Entity ReflectionComposeLayout { get; }
    public Entity ReflectionTrace { get; }
    public Entity ReflectionCompose { get; }
    public Entity ReflectionHistoryLayout { get; }
    public Entity ReflectionCommitLayout { get; }
    public Entity ReflectionCommitHitLayout { get; }
    public Entity ReflectionHistory { get; }
    public Entity ReflectionCommit { get; }
    public Entity ReflectionCommitHit { get; }

    public Entity Forward { get; }
    public Entity ForwardDouble { get; }
    public Entity ForwardBackground { get; }

    public Entity Raster { get; }
    public Entity RasterDouble { get; }
    public Entity StreamRaster { get; }
    public Entity StreamRasterDouble { get; }
    public Entity StreamShadow { get; }
    public Entity StreamShadowDouble { get; }

    public Entity Shadow { get; }
    public Entity ShadowDouble { get; }

    public Entity Cluster { get; }
    public Entity TileReset { get; }
    public Entity TileClassify { get; }
    public Entity Background { get; }
    public Entity Resolve { get; }
    public Entity ResolveDirect { get; }

    public Entity Coverage { get; }
    public Entity CoverageDouble { get; }
    public Entity Transparent { get; }
    public Entity TransparentDouble { get; }
    public Entity Transmission { get; }
    public Entity TransmissionDouble { get; }

    public Entity Output { get; }

    public PbrPipelines(in GpuFrame frame, WGPUTextureFormat format, bool surfaceData, PbrOpaquePath opaquePath, bool sceneGi, bool gpuStream = false, bool staticLightmap = false, bool residentInstances = false, bool reflectionCaptures = false, bool liveReflections = false, bool reflectionHistory = false,bool rasterPositionTables=false,bool cpuStreamInstances=false,bool autoExposure=false)
    {
        _sourceWorld = frame.ResourceWorld;
        _gpu = new(frame, 0);
        _sceneGi = sceneGi;
        _staticLightmap = staticLightmap;
        _residentInstances = residentInstances;
        _rasterPositionTables=rasterPositionTables;
        _reflectionCaptures = reflectionCaptures;
        _liveReflections = liveReflections;
        _cpuStreamInstances = cpuStreamInstances;
        try {
            var limits = _gpu.Limits;
            var vertexPlanes=residentInstances?PbrRasterVertex.Planes+1:StreamGeometryPage.VertexPlanes;
            if (limits.MaxVertexBuffers < vertexPlanes || limits.MaxVertexAttributes < vertexPlanes)
                throw new NotSupportedException("PBR vertex input requires four attribute planes.");
            if (gpuStream && limits.MaxStorageBuffersPerShaderStage < 8)
                throw new NotSupportedException("Instanced GPU visibility requires eight storage buffers per shader stage.");
            if (residentInstances && limits.MaxStorageBuffersPerShaderStage < 7)
                throw new NotSupportedException("Shared Raster visibility requires seven storage buffers per shader stage.");
            if (cpuStreamInstances && limits.MaxStorageBuffersPerShaderStage < 7)
                throw new NotSupportedException("Shared CPU stream visibility requires seven storage buffers per shader stage.");
            if(rasterPositionTables && (!residentInstances || limits.MaxStorageBuffersPerShaderStage<8))
                throw new NotSupportedException("Raster position tables require eight compute storage buffers.");
            if (limits.MaxSampledTexturesPerShaderStage < 10 + (sceneGi ? 1 : 0) + (staticLightmap ? 3 : 0) + (reflectionCaptures ? 1 : 0))
                throw new NotSupportedException("PBR frame lighting exceeds the device sampled-texture limit.");
            if (limits.MaxStorageBuffersPerShaderStage < 6 || limits.MaxBindGroups < 4 || limits.MaxSampledTexturesPerShaderStage < 10 || limits.MaxSamplersPerShaderStage < 8 || limits.MaxComputeInvocationsPerWorkgroup < 64)
                throw new NotSupportedException("Fused PBR requires 4 bind groups, 6 storage buffers, 10 sampled textures, 8 samplers and 64 compute lanes.");
            if (surfaceData && limits.MaxStorageTexturesPerShaderStage < 3)
                throw new NotSupportedException("Visibility surface export requires three compute storage textures.");
            if (liveReflections && (!surfaceData || !sceneGi || opaquePath != PbrOpaquePath.Visibility || limits.MaxStorageTexturesPerShaderStage < 4))
                throw new NotSupportedException("Live reflections require shared transport, Visibility surfaces and four compute storage textures.");
            if (liveReflections && limits.MaxTextureArrayLayers < PbrReflectionStorage.Layers(3))
                throw new NotSupportedException("Live reflections require twelve byte-plane texture array layers.");
            var frameEntries = new List<WGPUBindGroupLayoutEntry> {
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, k_All, 512),
                GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, k_All),
                GpuBinding.Buffer(2, WGPUBufferBindingType.ReadOnlyStorage, k_Shade),
                GpuBinding.Texture(3, WGPUTextureSampleType.Depth, k_Shade, WGPUTextureViewDimension._2DArray),
                GpuBinding.Texture(4, WGPUTextureSampleType.Float, k_Shade, WGPUTextureViewDimension.Cube),
                GpuBinding.Texture(5, WGPUTextureSampleType.Float, k_Shade),
                GpuBinding.Sampler(6, k_Shade),
                GpuBinding.Sampler(7, k_Shade),
                new WGPUBindGroupLayoutEntry {
                    Binding = 11,
                    Visibility = k_Shade,
                    Sampler = new() { Type = WGPUSamplerBindingType.Comparison }
                },
                GpuBinding.Buffer(8, WGPUBufferBindingType.Uniform, k_Shade, 144)
            };
            if (sceneGi) {
                frameEntries.Add(GpuBinding.Texture(9, WGPUTextureSampleType.Float, k_Shade, WGPUTextureViewDimension._3D));
                frameEntries.Add(GpuBinding.Buffer(10, WGPUBufferBindingType.Uniform, k_Shade, 48));
            }
            if (staticLightmap) {
                frameEntries.Add(GpuBinding.Texture(12, WGPUTextureSampleType.Uint, k_Shade));
                frameEntries.Add(GpuBinding.Texture(13, WGPUTextureSampleType.Uint, k_Shade));
                frameEntries.Add(GpuBinding.Texture(14, WGPUTextureSampleType.Uint, k_Shade, WGPUTextureViewDimension._2DArray));
            }
            if (reflectionCaptures) {
                frameEntries.Add(GpuBinding.Texture(15, WGPUTextureSampleType.Float, k_Shade, WGPUTextureViewDimension._2DArray));
                frameEntries.Add(GpuBinding.Buffer(16, WGPUBufferBindingType.Uniform, k_Shade, PbrReflectionCaptureGpu.UniformBytes));
            }
            FrameLayout = GpuBinding.Layout(_gpu, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(frameEntries));
            RasterFrameLayout = GpuBinding.Layout(_gpu, [
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Vertex, 512),
                GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Vertex)
            ]);
            ClusterLayout = GpuBinding.Layout(_gpu, [
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 512),
                GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(2, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)
            ]);
            if(rasterPositionTables) {
                GeometryLayout=GpuBinding.Layout(_gpu,[
                    GpuBinding.Buffer(0,WGPUBufferBindingType.ReadOnlyStorage,WGPUShaderStage.Vertex|WGPUShaderStage.Compute),
                    GpuBinding.Buffer(1,WGPUBufferBindingType.ReadOnlyStorage,WGPUShaderStage.Compute),
                    GpuBinding.Buffer(2,WGPUBufferBindingType.ReadOnlyStorage,WGPUShaderStage.Vertex|WGPUShaderStage.Compute),
                    GpuBinding.Buffer(3,WGPUBufferBindingType.ReadOnlyStorage,WGPUShaderStage.Vertex|WGPUShaderStage.Compute)]);
                RasterGeometryLayout=GpuBinding.Layout(_gpu,[
                    GpuBinding.Buffer(0,WGPUBufferBindingType.ReadOnlyStorage,WGPUShaderStage.Vertex|WGPUShaderStage.Compute),
                    GpuBinding.Buffer(1,WGPUBufferBindingType.ReadOnlyStorage,WGPUShaderStage.Vertex|WGPUShaderStage.Compute),
                    GpuBinding.Buffer(2,WGPUBufferBindingType.ReadOnlyStorage,WGPUShaderStage.Vertex|WGPUShaderStage.Compute),
                    GpuBinding.Buffer(3,WGPUBufferBindingType.ReadOnlyStorage,WGPUShaderStage.Compute)]);
            }
            else {
                GeometryLayout = GpuBinding.Layout(_gpu, Enumerable.Range(0, gpuStream || cpuStreamInstances || residentInstances ? 3 : 2).Select(i => GpuBinding.Buffer((uint)i, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Vertex | WGPUShaderStage.Compute)).ToArray());
                RasterGeometryLayout=GeometryLayout;
            }
            var material = new WGPUBindGroupLayoutEntry[12];
            material[0] = GpuBinding.Buffer(0, WGPUBufferBindingType.ReadOnlyStorage, k_Shade, 96);
            material[1] = GpuBinding.Buffer(1, WGPUBufferBindingType.Uniform, k_Shade, 16);
            for (uint i = 0; i < 5; i++) {
                material[2 + (i * 2)] = GpuBinding.Texture(2 + (i * 2), WGPUTextureSampleType.Float, k_Shade, WGPUTextureViewDimension._2DArray);
                material[3 + (i * 2)] = GpuBinding.Sampler(3 + (i * 2), k_Shade);
            }

            MaterialLayout = GpuBinding.Layout(_gpu, material);
            var resolveEntries = new List<WGPUBindGroupLayoutEntry> {
                GpuBinding.Texture(0, WGPUTextureSampleType.Uint, WGPUShaderStage.Compute),
                GpuBinding.Texture(1, WGPUTextureSampleType.Depth, WGPUShaderStage.Compute),
                GpuBinding.Buffer(2, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.StorageTexture(3, liveReflections ? PbrReflectionStorage.Format : WGPUTextureFormat.RGBA16Float,
                    liveReflections ? WGPUTextureViewDimension._2DArray : WGPUTextureViewDimension._2D)
            };
            if (surfaceData) {
                resolveEntries.Add(GpuBinding.StorageTexture(4, WGPUTextureFormat.RGBA16Float));
                resolveEntries.Add(GpuBinding.StorageTexture(5, WGPUTextureFormat.RGBA8Unorm));
            }
            if (gpuStream) resolveEntries.Add(GpuBinding.Buffer(6, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute));
            if (liveReflections) resolveEntries.Add(GpuBinding.StorageTexture(7, PbrReflectionStorage.Format, WGPUTextureViewDimension._2DArray));
            ResolveLayout = GpuBinding.Layout(_gpu, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(resolveEntries));
            var tileEntries = new List<WGPUBindGroupLayoutEntry> {
                GpuBinding.Texture(0, WGPUTextureSampleType.Uint, WGPUShaderStage.Compute),
                GpuBinding.Buffer(2, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)
            };
            if (gpuStream) tileEntries.Add(GpuBinding.Buffer(6, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute));
            TileLayout = GpuBinding.Layout(_gpu, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tileEntries));
            GlassLayout = GpuBinding.Layout(_gpu, [
                GpuBinding.Texture(0, WGPUTextureSampleType.Float, WGPUShaderStage.Fragment),
                GpuBinding.Texture(1, WGPUTextureSampleType.Depth, WGPUShaderStage.Fragment)
            ]);
            OutputLayout = GpuBinding.Layout(_gpu, [
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Fragment, 16),
                GpuBinding.Texture(1, WGPUTextureSampleType.Float, WGPUShaderStage.Fragment),
                GpuBinding.Sampler(2, WGPUShaderStage.Fragment)
            ]);
            if (autoExposure) {
                ExposureLayout = GpuBinding.Layout(_gpu, [
                    GpuBinding.Buffer(0, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute, 16),
                    GpuBinding.Texture(1, WGPUTextureSampleType.Float, WGPUShaderStage.Compute),
                    GpuBinding.Buffer(2, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute, 2048),
                    GpuBinding.Buffer(3, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 16)
                ]);
                var exposure = Module("exposure.wgsl");
                var exposureLayout = GpuBinding.PipelineLayout(_gpu, ExposureLayout);
                ExposureMeasure = Compute(exposure, exposureLayout, "measure_exposure");
                ExposureReduce = Compute(exposure, exposureLayout, "reduce_exposure");
            }
            if (opaquePath == PbrOpaquePath.ForwardPlus) {
                BackgroundLayout = GpuBinding.Layout(_gpu, [
                    GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 512),
                    GpuBinding.Texture(1, WGPUTextureSampleType.Float, WGPUShaderStage.Compute, WGPUTextureViewDimension.Cube),
                    GpuBinding.Sampler(2, WGPUShaderStage.Compute),
                    GpuBinding.StorageTexture(3, WGPUTextureFormat.RGBA16Float),
                    GpuBinding.Texture(4, WGPUTextureSampleType.Depth, WGPUShaderStage.Compute),
                    new WGPUBindGroupLayoutEntry {
                        Binding = 5,
                        Visibility = WGPUShaderStage.Compute,
                        Sampler = new() { Type = WGPUSamplerBindingType.Comparison }
                    }
                ]);
            }
            var geometry = GpuBinding.PipelineLayout(_gpu, RasterFrameLayout, RasterGeometryLayout);
            var core = WgpuUnsafe.wgpuDeviceHasFeature((WGPUDevice*)_gpu.Device.DangerousGetHandle(), WGPUFeatureName.CoreFeaturesAndLimits) != 0;
            SeparateDirectResolvePass = !core;
            var rasterFragment = core ? "raster_fragment" : "raster_fragment_depth";
            var shadowFragment = core ? null : "shadow_fragment_depth";
            var rasterShader = Module("raster.wgsl", streamInstances: gpuStream);
            if (!gpuStream) {
                Raster = Render(rasterShader, geometry, "raster_vertex", rasterFragment, WGPUTextureFormat.R32Uint, true, WGPUCullMode.Back);
                RasterDouble = Render(rasterShader, geometry, "raster_vertex", rasterFragment, WGPUTextureFormat.R32Uint, true, WGPUCullMode.None);
                Shadow = Render(rasterShader, geometry, "shadow_vertex", shadowFragment, null, true, WGPUCullMode.Back, shadowBias: true);
                ShadowDouble = Render(rasterShader, geometry, "shadow_vertex", shadowFragment, null, true, WGPUCullMode.None, shadowBias: true);
            }
            if (gpuStream) {
                StreamWorkLayout = GpuBinding.Layout(_gpu, [
                    GpuBinding.Buffer(12, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Vertex),
                    GpuBinding.Buffer(13, WGPUBufferBindingType.Uniform, WGPUShaderStage.Vertex,
                        (ulong)System.Runtime.InteropServices.Marshal.SizeOf<PbrGpuHierarchy.Configuration>())
                ]);
                var streamLayout = GpuBinding.PipelineLayout(_gpu, RasterFrameLayout, GeometryLayout, StreamWorkLayout);
                StreamRaster = Render(rasterShader, streamLayout, "stream_vertex", rasterFragment, WGPUTextureFormat.R32Uint, true, WGPUCullMode.Back);
                StreamRasterDouble = Render(rasterShader, streamLayout, "stream_vertex", rasterFragment, WGPUTextureFormat.R32Uint, true, WGPUCullMode.None);
                StreamShadow = Render(rasterShader, streamLayout, "stream_shadow_vertex", shadowFragment, null, true, WGPUCullMode.Back, shadowBias: true);
                StreamShadowDouble = Render(rasterShader, streamLayout, "stream_shadow_vertex", shadowFragment, null, true, WGPUCullMode.None, shadowBias: true);
            }
            if (opaquePath == PbrOpaquePath.ForwardPlus) {
                var forward = Module("forward.wgsl");
                var forwardLayout = GpuBinding.PipelineLayout(_gpu, FrameLayout, GeometryLayout, MaterialLayout);
                var forwardFragment = core ? "forward_fragment" : "forward_fragment_depth";
                Forward = Render(forward, forwardLayout, "forward_vertex", forwardFragment, WGPUTextureFormat.RGBA16Float, true, WGPUCullMode.Back);
                ForwardDouble = Render(forward, forwardLayout, "forward_vertex", forwardFragment, WGPUTextureFormat.RGBA16Float, true, WGPUCullMode.None);
            }
            var clusterShader = Module("clusters.wgsl", writableClusters: true);
            Cluster = Compute(clusterShader, GpuBinding.PipelineLayout(_gpu, ClusterLayout), "cull");
            var tiles = Module("tiles.wgsl", streamInstances: gpuStream, shadingWork: gpuStream,
                compatibilityUniformPadding: !core);
            var tilePipelineLayout = GpuBinding.PipelineLayout(_gpu, FrameLayout, GeometryLayout, MaterialLayout, TileLayout);
            TileReset = Compute(tiles, tilePipelineLayout, "reset_tiles");
            TileClassify = Compute(tiles, tilePipelineLayout, "classify");
            var shade = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device,
                PbrShaderSource.Compile(_sourceWorld, "resolve.wgsl", surfaceData: surfaceData, sceneGi: sceneGi,
                    streamInstances: gpuStream || cpuStreamInstances, shadingWork: gpuStream, staticLightmap: staticLightmap, residentInstances: residentInstances, reflectionCaptures: reflectionCaptures, liveReflections: liveReflections,rasterPositionTables:rasterPositionTables,cpuStreamInstances:cpuStreamInstances), "pbr-resolve"));
            var shading = GpuBinding.PipelineLayout(_gpu, FrameLayout, GeometryLayout, MaterialLayout, ResolveLayout);
            Background = Compute(shade, shading, "background");
            Resolve = Compute(shade, shading, "resolve");
            ResolveDirect = Compute(shade, shading, "resolve_direct");
            if (liveReflections) {
                // Read Depth32Float as an unfiltered float view: Compatibility forbids textureLoad on a depth binding.
                ReflectionTraceLayout = GpuBinding.Layout(_gpu, [
                    GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 512),
                    GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                    GpuBinding.Buffer(2, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                    GpuBinding.Buffer(3, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                    GpuBinding.Buffer(4, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                    GpuBinding.Buffer(5, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 32),
                    GpuBinding.Texture(6, WGPUTextureSampleType.UnfilterableFloat, WGPUShaderStage.Compute),
                    GpuBinding.Texture(7, WGPUTextureSampleType.Float, WGPUShaderStage.Compute),
                    GpuBinding.Texture(8, WGPUTextureSampleType.Float, WGPUShaderStage.Compute, WGPUTextureViewDimension._2DArray),
                    GpuBinding.StorageTexture(9, PbrReflectionStorage.Format, WGPUTextureViewDimension._2DArray),
                    GpuBinding.Texture(10, WGPUTextureSampleType.Float, WGPUShaderStage.Compute, WGPUTextureViewDimension._3D),
                    GpuBinding.Sampler(11, WGPUShaderStage.Compute),
                    GpuBinding.Buffer(12, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 48),
                    GpuBinding.Buffer(13, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 144),
                    GpuBinding.StorageTexture(14, PbrReflectionStorage.Format, WGPUTextureViewDimension._2DArray),
                    GpuBinding.Buffer(15, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                    GpuBinding.Buffer(16, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute)
                ]);
                ReflectionComposeLayout = GpuBinding.Layout(_gpu, [
                    GpuBinding.Texture(0, WGPUTextureSampleType.Float, WGPUShaderStage.Compute, WGPUTextureViewDimension._2DArray),
                    GpuBinding.Texture(1, WGPUTextureSampleType.Float, WGPUShaderStage.Compute, WGPUTextureViewDimension._2DArray),
                    GpuBinding.Texture(2, WGPUTextureSampleType.Float, WGPUShaderStage.Compute, WGPUTextureViewDimension._2DArray),
                    GpuBinding.StorageTexture(3, WGPUTextureFormat.RGBA16Float),
                    GpuBinding.Buffer(4, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 16)
                ]);
                ReflectionTrace = Compute(Module("reflection_trace.wgsl"), GpuBinding.PipelineLayout(_gpu, ReflectionTraceLayout), "trace_reflections");
                ReflectionCompose = Compute(Module("reflection_compose.wgsl"), GpuBinding.PipelineLayout(_gpu, ReflectionComposeLayout), "compose_reflections");
                if (reflectionHistory) {
                    var history = new List<WGPUBindGroupLayoutEntry> {
                        GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 512),
                        GpuBinding.Buffer(1, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 112),
                        GpuBinding.Buffer(2, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                        GpuBinding.Buffer(3, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute)
                    };
                    for (uint binding = 4; binding <= 13; binding++) history.Add(GpuBinding.Texture(binding,
                        binding == 8 ? WGPUTextureSampleType.UnfilterableFloat : WGPUTextureSampleType.Float,
                        WGPUShaderStage.Compute, binding is 7 or 8 or 11 ? WGPUTextureViewDimension._2D : WGPUTextureViewDimension._2DArray));
                    history.Add(GpuBinding.StorageTexture(14, PbrReflectionStorage.Format, WGPUTextureViewDimension._2DArray));
                    ReflectionHistoryLayout = GpuBinding.Layout(_gpu, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(history));
                    var commit = new List<WGPUBindGroupLayoutEntry> {
                        GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 512),
                        GpuBinding.Buffer(1, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 112)
                    };
                    for (uint binding = 2; binding <= 6; binding++) commit.Add(GpuBinding.Texture(binding,
                        binding == 6 ? WGPUTextureSampleType.UnfilterableFloat : WGPUTextureSampleType.Float,
                        WGPUShaderStage.Compute, binding is 5 or 6 ? WGPUTextureViewDimension._2D : WGPUTextureViewDimension._2DArray));
                    var hit = new List<WGPUBindGroupLayoutEntry>(commit) { GpuBinding.StorageTexture(7, PbrReflectionStorage.Format, WGPUTextureViewDimension._2DArray) };
                    ReflectionCommitHitLayout = GpuBinding.Layout(_gpu, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(hit));
                    commit.Add(GpuBinding.StorageTexture(7, PbrReflectionStorage.Format, WGPUTextureViewDimension._2DArray));
                    commit.Add(GpuBinding.StorageTexture(8, PbrReflectionStorage.Format, WGPUTextureViewDimension._2DArray));
                    commit.Add(GpuBinding.StorageTexture(9, WGPUTextureFormat.RGBA16Float));
                    commit.Add(GpuBinding.StorageTexture(10, PbrReflectionStorage.Format, WGPUTextureViewDimension._2DArray));
                    ReflectionCommitLayout = GpuBinding.Layout(_gpu, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(commit));
                    ReflectionHistory = Compute(Module("reflection_history.wgsl"), GpuBinding.PipelineLayout(_gpu, ReflectionHistoryLayout), "filter_reflections");
                    ReflectionCommit = Compute(Module("reflection_history_commit.wgsl"), GpuBinding.PipelineLayout(_gpu, ReflectionCommitLayout), "commit_reflections");
                    var hitSource = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device, PbrShaderSource.Compile(_sourceWorld,
                        "reflection_history_commit.wgsl", historyCommitHits: true), "reflection-history-hit-commit"));
                    ReflectionCommitHit = Compute(hitSource, GpuBinding.PipelineLayout(_gpu, ReflectionCommitHitLayout), "commit_reflections");
                }
            }
            if (opaquePath == PbrOpaquePath.ForwardPlus)
                ForwardBackground = Compute(Module("forward_background.wgsl"), GpuBinding.PipelineLayout(_gpu, BackgroundLayout), "forward_background");
            var transparent = Module("transparent.wgsl", streamInstances: gpuStream);
            var glassLayout = GpuBinding.PipelineLayout(_gpu, FrameLayout, GeometryLayout, MaterialLayout, GlassLayout);
            if (opaquePath == PbrOpaquePath.ForwardPlus) {
                var coverageLayout = GpuBinding.PipelineLayout(_gpu, RasterFrameLayout, GeometryLayout, MaterialLayout);
                Coverage = Render(transparent, coverageLayout, "transparent_vertex", "coverage_fragment", null, true, WGPUCullMode.Back);
                CoverageDouble = Render(transparent, coverageLayout, "transparent_vertex", "coverage_fragment", null, true, WGPUCullMode.None);
            }
            Transparent = Render(transparent, glassLayout, "transparent_vertex", "transparent_fragment", WGPUTextureFormat.RGBA16Float, false, WGPUCullMode.Back, true);
            TransparentDouble = Render(transparent, glassLayout, "transparent_vertex", "transparent_fragment", WGPUTextureFormat.RGBA16Float, false, WGPUCullMode.None, true);
            Transmission = Render(transparent, glassLayout, "transparent_vertex", "transmission_fragment", WGPUTextureFormat.RGBA16Float, false, WGPUCullMode.Back, true);
            TransmissionDouble = Render(transparent, glassLayout, "transparent_vertex", "transmission_fragment", WGPUTextureFormat.RGBA16Float, false, WGPUCullMode.None, true);
            var output = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device, PbrShaderSource.Compile(_sourceWorld, "output.wgsl"), "pbr-output"));
            Output = Render(output, GpuBinding.PipelineLayout(_gpu, OutputLayout), "output_vertex", "output_fragment", format, false, WGPUCullMode.None);
        }
        catch {
            _gpu.Dispose();
            throw;
        }
    }

    private Entity Module(string file, bool writableClusters = false, bool streamInstances = false, bool shadingWork = false,
        bool compatibilityUniformPadding = false)
        => _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device,
            PbrShaderSource.Compile(_sourceWorld, file, writableClusters, sceneGi: _sceneGi,
                streamInstances: streamInstances || _cpuStreamInstances, shadingWork: shadingWork,
                compatibilityUniformPadding: compatibilityUniformPadding, staticLightmap: _staticLightmap, residentInstances: _residentInstances, reflectionCaptures: _reflectionCaptures, liveReflections: _liveReflections,rasterPositionTables:_rasterPositionTables,cpuStreamInstances:_cpuStreamInstances), file));

    private Entity Compute(Entity shader, Entity layout, string entry)
    {
        var name = Encoding.UTF8.GetBytes(entry);
        fixed (byte* pointer = name) {
            var descriptor = WGPUComputePipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)layout.GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Compute.Module = (WGPUShaderModule*)shader.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            descriptor.Compute.EntryPoint = new() {
                Data = pointer,
                Length = (nuint)name.Length
            };
            return _gpu.Own(Wgpu.CreateComputePipeline(_gpu.Device, descriptor));
        }
    }

    private Entity Render(Entity shader, Entity layout, string vertex, string? fragment, WGPUTextureFormat? format, bool depthWrite, WGPUCullMode cull, bool blend = false, bool shadowBias = false)
    {
        var vs = Encoding.UTF8.GetBytes(vertex);
        var fs = Encoding.UTF8.GetBytes(fragment ?? "");
        fixed (byte* v = vs)
        fixed (byte* f = fs) {
            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)layout.GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Vertex.Module = (WGPUShaderModule*)shader.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            descriptor.Vertex.EntryPoint = new() {
                Data = v,
                Length = (nuint)vs.Length
            };
            var attributeCount = _rasterPositionTables?0:vertex is "forward_vertex" or "transparent_vertex" ? (_residentInstances?PbrRasterVertex.Planes:StreamGeometryPage.VertexPlanes) : vertex is "shadow_vertex" ? 1 : 0;
            var instanceInput = _residentInstances && (vertex is "forward_vertex" or "transparent_vertex" or "shadow_vertex" or "raster_vertex");
            if (instanceInput) attributeCount++;
            var attributes = stackalloc WGPUVertexAttribute[StreamGeometryPage.VertexPlanes];
            var buffers = stackalloc WGPUVertexBufferLayout[StreamGeometryPage.VertexPlanes];
            for (var index = 0; index < attributeCount; index++) {
                if (instanceInput && index == attributeCount - 1) {
                    attributes[index] = new() { Format = WGPUVertexFormat.Uint32x2, ShaderLocation = 3 };
                    buffers[index] = new() { ArrayStride = 8, StepMode = WGPUVertexStepMode.Instance, AttributeCount = 1, Attributes = &attributes[index] };
                    continue;
                }
                var attributeFormat = index == 0 ? WGPUVertexFormat.Float32x4 : _residentInstances
                    ? index == 1 ? WGPUVertexFormat.Uint32x4 : WGPUVertexFormat.Uint32x2
                    : index == 3 ? WGPUVertexFormat.Uint32 : WGPUVertexFormat.Uint32x4;
                attributes[index] = new() { Format = attributeFormat, ShaderLocation = (uint)index };
                buffers[index] = new() { ArrayStride = _residentInstances ? index == 2 ? 8ul : 16ul : StreamGeometryPage.PlaneStride((uint)index), StepMode = WGPUVertexStepMode.Vertex, AttributeCount = 1, Attributes = &attributes[index] };
            }
            descriptor.Vertex.BufferCount = (nuint)attributeCount;
            descriptor.Vertex.Buffers = attributeCount == 0 ? null : buffers;
            descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
            descriptor.Primitive.FrontFace = WGPUFrontFace.CCW;
            descriptor.Primitive.CullMode = cull;
            var target = WGPUColorTargetState.Default;
            target.Format = format ?? WGPUTextureFormat.Undefined;
            target.WriteMask = WGPUColorWriteMask.All;
            var blending = WGPUBlendState.Default;
            blending.Color.SrcFactor = WGPUBlendFactor.SrcAlpha;
            blending.Color.DstFactor = WGPUBlendFactor.OneMinusSrcAlpha;
            blending.Alpha.SrcFactor = WGPUBlendFactor.One;
            blending.Alpha.DstFactor = WGPUBlendFactor.OneMinusSrcAlpha;
            if (blend) target.Blend = &blending;
            var frag = WGPUFragmentState.Default;
            frag.Module = descriptor.Vertex.Module;
            frag.EntryPoint = new() {
                Data = f,
                Length = (nuint)fs.Length
            };
            frag.TargetCount = format.HasValue ? 1u : 0u;
            frag.Targets = format.HasValue ? &target : null;
            if (fragment is not null) descriptor.Fragment = &frag;
            var depth = WGPUDepthStencilState.Default;
            depth.Format = WGPUTextureFormat.Depth32Float;
            depth.DepthWriteEnabled = depthWrite ? WGPUOptionalBool.True : WGPUOptionalBool.False;
            depth.DepthCompare = blend ? WGPUCompareFunction.LessEqual : WGPUCompareFunction.Less;
            if (shadowBias) {
                depth.DepthBias = 2;
                depth.DepthBiasSlopeScale = 2;
            }

            if (depthWrite || blend) descriptor.DepthStencil = &depth;
            return _gpu.Own(Wgpu.CreateRenderPipeline(_gpu.Device, descriptor));
        }
    }

    public void Dispose() => _gpu.Dispose();
}
