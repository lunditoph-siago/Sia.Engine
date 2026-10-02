using System.Text;
using Sia;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe class PbrPipelines : IDisposable
{
    private const WGPUShaderStage k_All = WGPUShaderStage.Compute | WGPUShaderStage.Vertex | WGPUShaderStage.Fragment;
    private const WGPUShaderStage k_Shade = WGPUShaderStage.Compute | WGPUShaderStage.Fragment;

    private readonly GpuResources _gpu;
    private readonly bool _sceneGi;

    public Entity FrameLayout { get; }
    public Entity RasterFrameLayout { get; }
    public Entity ClusterLayout { get; }
    public Entity GeometryLayout { get; }
    public Entity MaterialLayout { get; }
    public Entity ResolveLayout { get; }
    public Entity TileLayout { get; }
    public Entity GlassLayout { get; }
    public Entity OutputLayout { get; }
    public Entity BackgroundLayout { get; }
    public Entity StreamWorkLayout { get; }

    public Entity Forward { get; }
    public Entity ForwardDouble { get; }
    public Entity Prepass { get; }
    public Entity PrepassDouble { get; }
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

    public PbrPipelines(in GpuFrame frame, WGPUTextureFormat format, bool surfaceData, PbrOpaquePath opaquePath, bool sceneGi, bool gpuStream = false)
    {
        _gpu = new(frame, 0);
        _sceneGi = sceneGi;
        try {
            var limits = _gpu.Limits;
            if (gpuStream && limits.MaxStorageBuffersPerShaderStage < 8)
                throw new NotSupportedException("Instanced GPU visibility requires eight storage buffers per shader stage.");
            if (sceneGi && limits.MaxSampledTexturesPerShaderStage < 11)
                throw new NotSupportedException("Scene probes require eleven sampled textures.");
            if (limits.MaxStorageBuffersPerShaderStage < 6 || limits.MaxBindGroups < 4 || limits.MaxSampledTexturesPerShaderStage < 10 || limits.MaxSamplersPerShaderStage < 8 || limits.MaxComputeInvocationsPerWorkgroup < 64)
                throw new NotSupportedException("Fused PBR requires 4 bind groups, 6 storage buffers, 10 sampled textures, 8 samplers and 64 compute lanes.");
            if (surfaceData && limits.MaxStorageTexturesPerShaderStage < 3)
                throw new NotSupportedException("Visibility surface export requires three compute storage textures.");
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
            GeometryLayout = GpuBinding.Layout(_gpu, Enumerable.Range(0, gpuStream ? 3 : 2).Select(i => GpuBinding.Buffer((uint)i, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Vertex | WGPUShaderStage.Compute)).ToArray());
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
                GpuBinding.StorageTexture(3, WGPUTextureFormat.RGBA16Float)
            };
            if (surfaceData) {
                resolveEntries.Add(GpuBinding.StorageTexture(4, WGPUTextureFormat.RGBA16Float));
                resolveEntries.Add(GpuBinding.StorageTexture(5, WGPUTextureFormat.RGBA8Unorm));
            }
            if (gpuStream) resolveEntries.Add(GpuBinding.Buffer(6, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute));
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
            var geometry = GpuBinding.PipelineLayout(_gpu, RasterFrameLayout, GeometryLayout);
            var core = WgpuUnsafe.wgpuDeviceHasFeature((WGPUDevice*)_gpu.Device.DangerousGetHandle(), WGPUFeatureName.CoreFeaturesAndLimits) != 0;
            var rasterFragment = core ? "raster_fragment" : "raster_fragment_depth";
            var rasterShader = Module("raster.wgsl", streamInstances: gpuStream);
            if (!gpuStream) {
                Raster = Render(rasterShader, geometry, "raster_vertex", rasterFragment, WGPUTextureFormat.R32Uint, true, WGPUCullMode.Back);
                RasterDouble = Render(rasterShader, geometry, "raster_vertex", rasterFragment, WGPUTextureFormat.R32Uint, true, WGPUCullMode.None);
                Shadow = Render(rasterShader, geometry, "shadow_vertex", null, null, true, WGPUCullMode.Back, shadowBias: true);
                ShadowDouble = Render(rasterShader, geometry, "shadow_vertex", null, null, true, WGPUCullMode.None, shadowBias: true);
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
                StreamShadow = Render(rasterShader, streamLayout, "stream_shadow_vertex", null, null, true, WGPUCullMode.Back, shadowBias: true);
                StreamShadowDouble = Render(rasterShader, streamLayout, "stream_shadow_vertex", null, null, true, WGPUCullMode.None, shadowBias: true);
            }
            if (opaquePath == PbrOpaquePath.ForwardPlus) {
                var forward = Module("forward.wgsl");
                var forwardLayout = GpuBinding.PipelineLayout(_gpu, FrameLayout, GeometryLayout, MaterialLayout);
                Forward = Render(forward, forwardLayout, "forward_vertex", "forward_fragment", WGPUTextureFormat.RGBA16Float, false, WGPUCullMode.Back, depthRead: true);
                ForwardDouble = Render(forward, forwardLayout, "forward_vertex", "forward_fragment", WGPUTextureFormat.RGBA16Float, false, WGPUCullMode.None, depthRead: true);
                Prepass = Render(forward, geometry, "prepass_vertex", null, null, true, WGPUCullMode.Back);
                PrepassDouble = Render(forward, geometry, "prepass_vertex", null, null, true, WGPUCullMode.None);
            }
            var clusterShader = Module("clusters.wgsl", writableClusters: true);
            Cluster = Compute(clusterShader, GpuBinding.PipelineLayout(_gpu, ClusterLayout), "cull");
            var tiles = Module("tiles.wgsl", streamInstances: gpuStream, shadingWork: gpuStream);
            var tilePipelineLayout = GpuBinding.PipelineLayout(_gpu, FrameLayout, GeometryLayout, MaterialLayout, TileLayout);
            TileReset = Compute(tiles, tilePipelineLayout, "reset_tiles");
            TileClassify = Compute(tiles, tilePipelineLayout, "classify");
            var shade = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device,
                PbrShaderSource.Compile("resolve.wgsl", surfaceData: surfaceData, sceneGi: sceneGi,
                    streamInstances: gpuStream, shadingWork: gpuStream), "pbr-resolve"));
            var shading = GpuBinding.PipelineLayout(_gpu, FrameLayout, GeometryLayout, MaterialLayout, ResolveLayout);
            Background = Compute(shade, shading, "background");
            Resolve = Compute(shade, shading, "resolve");
            ResolveDirect = Compute(shade, shading, "resolve_direct");
            if (opaquePath == PbrOpaquePath.ForwardPlus)
                ForwardBackground = Compute(Module("forward_background.wgsl"), GpuBinding.PipelineLayout(_gpu, BackgroundLayout), "forward_background");
            var transparent = Module("transparent.wgsl");
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
            var output = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device, PbrShaderSource.Compile("output.wgsl"), "pbr-output"));
            Output = Render(output, GpuBinding.PipelineLayout(_gpu, OutputLayout), "output_vertex", "output_fragment", format, false, WGPUCullMode.None);
        }
        catch {
            _gpu.Dispose();
            throw;
        }
    }

    private Entity Module(string file, bool writableClusters = false, bool streamInstances = false, bool shadingWork = false)
        => _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device,
            PbrShaderSource.Compile(file, writableClusters, sceneGi: _sceneGi,
                streamInstances: streamInstances, shadingWork: shadingWork), file));

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

    private Entity Render(Entity shader, Entity layout, string vertex, string? fragment, WGPUTextureFormat? format, bool depthWrite, WGPUCullMode cull, bool blend = false, bool depthRead = false, bool shadowBias = false)
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
            var attributeCount = vertex is "forward_vertex" or "transparent_vertex" ? 3 : vertex is "prepass_vertex" or "shadow_vertex" ? 1 : 0;
            var attributes = stackalloc WGPUVertexAttribute[3];
            var buffers = stackalloc WGPUVertexBufferLayout[3];
            for (var index = 0; index < attributeCount; index++) {
                attributes[index] = new() { Format = WGPUVertexFormat.Float32x4, ShaderLocation = (uint)index };
                buffers[index] = new() { ArrayStride = 16, StepMode = WGPUVertexStepMode.Vertex, AttributeCount = 1, Attributes = &attributes[index] };
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
            depth.DepthCompare = depthRead ? WGPUCompareFunction.Equal : blend ? WGPUCompareFunction.LessEqual : WGPUCompareFunction.Less;
            if (shadowBias) {
                depth.DepthBias = 2;
                depth.DepthBiasSlopeScale = 2;
            }

            if (depthWrite || blend || depthRead) descriptor.DepthStencil = &depth;
            return _gpu.Own(Wgpu.CreateRenderPipeline(_gpu.Device, descriptor));
        }
    }

    public void Dispose() => _gpu.Dispose();
}
