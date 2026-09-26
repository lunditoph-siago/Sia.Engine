using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia;
using Sia.Engine;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrRenderer(
    ClusterLightCullingPipeline cullingPipeline,
    PbrIblPrecomputePipelines iblPipelines,
    PbrOutputPipelines outputPipelines)
{
    private Entity _lightingLayout;
    private Entity _iblLayout;

    public PbrExtractedView ExtractFrame(
        PbrViewState state,
        in GpuFrame frame,
        Entity cameraEntity,
        ClusterGridConfig clusterConfig,
        ShadowAtlasConfig shadowConfig,
        Aabb? shadowBounds = null)
    {
        var matrices = cameraEntity.Get<CameraMatrices>();
        var extractedShadowConfig = Copy(shadowConfig);
        state.Shadows.Refresh(frame.MainWorld, extractedShadowConfig, cameraEntity, shadowBounds);
        state.Lights.Refresh(frame.MainWorld, state.Shadows);

        var environment = frame.MainWorld.AcquireAddon<EnvironmentLighting>();
        var sky = environment.Sky;
        var atmosphere = environment.Atmosphere;
        atmosphere?.Validate();
        ArgumentNullException.ThrowIfNull(sky);
        sky.Validate();
        var coefficients = atmosphere is not null || state.PreparedSky == sky ? null : IrradianceSh.Project(sky.Evaluate);
        return new PbrExtractedView(
            matrices,
            cameraEntity.Get<global::Sia.Engine.Camera.Camera>(),
            frame.MainWorld.AcquireAddon<Viewport>().Value,
            Copy(clusterConfig),
            extractedShadowConfig,
            sky,
            coefficients,
            atmosphere);
    }

    public void PrepareLighting(
        PbrViewState state,
        in GpuFrame frame,
        PbrExtractedView extracted)
    {
        EnsureSceneLayouts(in frame);
        var clusterConfig = extracted.ClusterConfig;
        var shadowConfig = extracted.ShadowConfig;
        var atlasResized = state.ShadowAtlas.EnsureCapacity(in frame, shadowConfig);
        var shadowLayersResized = state.Shadows.Upload(in frame, shadowConfig);
        var lightsResized = state.Lights.Upload(in frame);
        var buffersResized = state.ClusterBuffers.EnsureCapacity(in frame, clusterConfig);

        var camera = extracted.Camera;
        var matrices = extracted.CameraMatrices;
        var viewport = extracted.Viewport;
        state.ClusterBuffers.UpdateConfig(
            in frame, clusterConfig, in matrices, camera.Near, camera.Far,
            state.Lights.ClusteredLights.Count, (uint)viewport.Width, (uint)viewport.Height);
        if (lightsResized || buffersResized || !state.CullingBindGroup.IsValid) {
            EnsureCullingBindGroup(state, in frame);
        }
        if (lightsResized || buffersResized || atlasResized || shadowLayersResized || !state.LightingBindGroup.IsValid) {
            EnsureLightingBindGroup(state, in frame);
        }

        PrepareIbl(state, in frame, extracted);
    }

    public void PrepareIbl(PbrViewState state, in GpuFrame frame, PbrExtractedView extracted)
    {
        EnsureSceneLayouts(in frame);
        var created = state.Ibl.EnsureCapacity(in frame);
        if (created) {
            EnsureIblPrefilterBindGroups(state, in frame);
            EnsureIblBindGroup(state, in frame);
        }
        var wasAtmosphere = state.ActiveAtmosphere is not null;
        state.ActiveAtmosphere = extracted.Atmosphere;
        if (extracted.Atmosphere is { } atmosphere) {
            state.Atmosphere ??= new AtmosphereGpuState(frame, state);
            if (!state.Atmosphere.Prepare(frame, atmosphere, extracted.CameraMatrices, !wasAtmosphere)) {
                return;
            }
        } else if (state.PreparedSky == extracted.Sky) {
            return;
        }
        var mipCount = IblEnvironmentGpuStore.PrefilteredMipCount;
        for (var face = 0; face < 6; face++) {
            for (var mip = 0; mip < mipCount; mip++) {
                var roughness = mipCount > 1 ? (float)mip / (mipCount - 1) : 0.0f;
                Wgpu.WriteBuffer(
                    frame.Queue.GetWgpu<WGPUQueue>(),
                    state.IblPrefilterParamsBuffers[face * mipCount + mip].GetWgpu<WGPUBuffer>(),
                    0,
                    [new IblPrefilterParamsGpu(
                        new float4(roughness, mip == 0 ? 1 : 128, face, 0.0f),
                        SkyUniformData.From(extracted.Sky))]);
            }
        }
        if (extracted.Atmosphere is null) {
            var coefficients = extracted.IrradianceCoefficients ??
                throw new InvalidOperationException("The extracted view does not contain irradiance coefficients.");
            state.Ibl.UploadSh(in frame, coefficients);
        }
        state.PreparedSky = extracted.Atmosphere is null ? extracted.Sky : null;
        state.EnvironmentRevision++;
    }

    private static ClusterGridConfig Copy(ClusterGridConfig source) => new() {
        TilesX = source.TilesX,
        TilesY = source.TilesY,
        ZSlices = source.ZSlices,
        MaxLightIndicesPerCluster = source.MaxLightIndicesPerCluster
    };

    private static ShadowAtlasConfig Copy(ShadowAtlasConfig source) => new() {
        TileResolution = source.TileResolution,
        CascadeCount = source.CascadeCount,
        SceneBoundsDirectional = source.SceneBoundsDirectional,
        ConservativeCasterBounds = source.ConservativeCasterBounds,
        CascadeSplitLambda = source.CascadeSplitLambda,
        CascadeShadowPullback = source.CascadeShadowPullback,
        ShadowDistance = source.ShadowDistance,
        MaxShadowedSpotLights = source.MaxShadowedSpotLights
    };

    public void EncodeIblPrefilter(
        PbrViewState state,
        int face,
        int mip,
        WgpuHandle<WGPURenderPassEncoder> renderPass)
    {
        var index = face * IblEnvironmentGpuStore.PrefilteredMipCount + mip;
        if (state.ActiveAtmosphere is not null) {
            state.Atmosphere!.EncodePrefilter(index, renderPass);
            return;
        }
        Wgpu.SetRenderPipeline(renderPass, iblPipelines.PrefilterPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, state.IblPrefilterBindGroups[index].GetWgpu<WGPUBindGroup>());
        Wgpu.Draw(renderPass, vertexCount: 3);
    }

    public void EncodeIblBrdfLut(WgpuHandle<WGPURenderPassEncoder> renderPass)
    {
        Wgpu.SetRenderPipeline(renderPass, iblPipelines.BrdfLutPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.Draw(renderPass, vertexCount: 3);
    }

    private void EnsureIblPrefilterBindGroups(PbrViewState state, in GpuFrame frame)
    {
        var count = 6 * IblEnvironmentGpuStore.PrefilteredMipCount;
        if (state.IblPrefilterBindGroups.Length == count) {
            return;
        }
        state.IblPrefilterParamsBuffers = new Entity[count];
        state.IblPrefilterBindGroups = new Entity[count];
        for (var index = 0; index < count; index++) {
            var buffer = frame.ResourceWorld.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
                Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
                Size = IblPrefilterParamsGpu.Stride
            });
            state.IblPrefilterParamsBuffers[index] = buffer;
            state.IblPrefilterBindGroups[index] = frame.ResourceWorld.OwnWgpu(
                IblPrefilterBindGroupLayout.CreateBindGroup(
                    frame.Device.GetWgpu<WGPUDevice>(),
                    iblPipelines.PrefilterBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
                    buffer.GetWgpu<WGPUBuffer>()));
        }
    }

    private void EnsureIblBindGroup(PbrViewState state, in GpuFrame frame)
    {
        if (state.IblBindGroup.IsValid) {
            state.IblBindGroup.Destroy();
        }
        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        state.IblBindGroup = frame.ResourceWorld.OwnWgpu(PbrIblBindGroupLayout.CreateBindGroup(
            deviceHandle,
            _iblLayout.GetWgpu<WGPUBindGroupLayout>(),
            state.Ibl.ShBuffer.GetWgpu<WGPUBuffer>(),
            state.Ibl.PrefilteredSamplingView.GetWgpu<WGPUTextureView>(),
            state.Ibl.PrefilteredSampler.GetWgpu<WGPUSampler>(),
            state.Ibl.BrdfLutView.GetWgpu<WGPUTextureView>(),
            state.Ibl.BrdfLutSampler.GetWgpu<WGPUSampler>()));
    }

    public void EncodeClusterLightCulling(
        PbrViewState state,
        ClusterGridConfig clusterConfig,
        WgpuHandle<WGPUComputePassEncoder> computePass)
    {
        Wgpu.SetComputePipeline(computePass, cullingPipeline.ComputePipeline.GetWgpu<WGPUComputePipeline>());
        Wgpu.SetBindGroup(computePass, 0, state.CullingBindGroup.GetWgpu<WGPUBindGroup>());
        var workgroups = (clusterConfig.ClusterCount + 63) / 64;
        Wgpu.DispatchWorkgroups(computePass, workgroups);
    }

    private void EnsureCullingBindGroup(PbrViewState state, in GpuFrame frame)
    {
        if (state.CullingBindGroup.IsValid) {
            state.CullingBindGroup.Destroy();
        }

        state.CullingBindGroup = cullingPipeline.CreateBindGroup(in frame, state.ClusterBuffers, state.Lights);
    }

    private void EnsureLightingBindGroup(PbrViewState state, in GpuFrame frame)
    {
        if (state.LightingBindGroup.IsValid) {
            state.LightingBindGroup.Destroy();
        }

        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        state.LightingBindGroup = frame.ResourceWorld.OwnWgpu(PbrLightingBindGroupLayout.CreateBindGroup(
            deviceHandle,
            _lightingLayout.GetWgpu<WGPUBindGroupLayout>(),
            state.ClusterBuffers.ConfigBuffer.GetWgpu<WGPUBuffer>(),
            state.Lights.ClusteredBuffer.GetWgpu<WGPUBuffer>(), state.Lights.ClusteredCapacity,
            state.ClusterBuffers.LightGridBuffer.GetWgpu<WGPUBuffer>(), state.ClusterBuffers.LightGridSize,
            state.ClusterBuffers.LightIndexListBuffer.GetWgpu<WGPUBuffer>(), state.ClusterBuffers.LightIndexListCapacity,
            state.Lights.DirectionalBuffer.GetWgpu<WGPUBuffer>(),
            state.ShadowAtlas.SamplingView.GetWgpu<WGPUTextureView>(),
            state.ShadowAtlas.Sampler.GetWgpu<WGPUSampler>(),
            state.Shadows.LayerBuffer.GetWgpu<WGPUBuffer>(), state.Shadows.LayerBufferCapacity,
            state.Shadows.ConfigBuffer.GetWgpu<WGPUBuffer>()));
    }
}
