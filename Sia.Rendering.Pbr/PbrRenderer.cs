using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia;
using Sia.Engine;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrRenderer(
    PbrDepthPrepassPipeline depthPipeline,
    ForwardPbrPipeline forwardPipeline,
    PbrClusterLightCullingPipeline cullingPipeline,
    PbrShadowDepthPipeline shadowDepthPipeline,
    PbrIblPrecomputePipelines iblPipelines,
    PbrOutputPipelines outputPipelines)
{
    public PbrExtractedView ExtractFrame(
        PbrViewState state,
        in GpuFrame frame,
        Entity cameraEntity,
        ClusterGridConfig clusterConfig,
        ShadowAtlasConfig shadowConfig)
    {
        var cache = frame.MainWorld.AcquireAddon<PbrRenderCache>();
        cache.Refresh();

        var matrices = cameraEntity.Get<CameraMatrices>();
        var visible = cache.Cull(matrices.Frustum);
        var extractedShadowConfig = Copy(shadowConfig);
        state.Shadows.Refresh(frame.MainWorld, extractedShadowConfig, cameraEntity);
        state.Lights.Refresh(frame.MainWorld, state.Shadows);

        var environment = frame.MainWorld.AcquireAddon<EnvironmentLighting>();
        var sky = environment.Sky;
        var atmosphere = environment.Atmosphere;
        atmosphere?.Validate();
        ArgumentNullException.ThrowIfNull(sky);
        sky.Validate();
        var coefficients = atmosphere is not null || state.PreparedSky == sky ? null : IrradianceSh.Project(sky.Evaluate);
        var allItems = cache.MeshHandles
            .Select(static (mesh, index) => new PbrDrawItem(mesh, index))
            .ToArray();
        var visibleItems = visible
            .Select(index => new PbrDrawItem(cache.MeshHandles[index], index))
            .ToArray();
        return new PbrExtractedView(
            matrices,
            cameraEntity.Get<global::Sia.Engine.Camera.Camera>(),
            frame.MainWorld.AcquireAddon<Viewport>().Value,
            Copy(clusterConfig),
            extractedShadowConfig,
            cache.Data.ToArray(),
            allItems,
            visibleItems,
            sky,
            coefficients,
            atmosphere);
    }

    public void PrepareFrame(PbrViewState state, in GpuFrame frame, PbrExtractedView extracted)
    {
        var meshStore = frame.ResourceWorld.AcquireAddon<MeshGpuStore>();
        var meshRegistry = frame.ResourceWorld.AcquireAddon<MeshRegistry>();
        state.Meshes.Clear();
        foreach (var item in extracted.AllItems) {
            state.Meshes.TryAdd(item.Mesh, meshStore.GetOrUpload(in frame, meshRegistry, item.Mesh));
        }

        var matrices = extracted.CameraMatrices;
        state.CameraUniforms.Update(in frame, in matrices);

        var resized = state.Instances.Upload(in frame, extracted.Instances);
        if (resized || !state.DepthBindGroup.IsValid) {
            EnsureBindGroups(state, in frame);
        }
    }

    public void QueueOpaque(PbrExtractedView extracted, RenderPhase<PbrDrawItem> phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        phase.AddRange(extracted.VisibleItems);
        phase.Sort();
    }

    public void PrepareLighting(
        PbrViewState state,
        in GpuFrame frame,
        PbrExtractedView extracted)
    {
        var clusterConfig = extracted.ClusterConfig;
        var shadowConfig = extracted.ShadowConfig;
        var atlasResized = state.ShadowAtlas.EnsureCapacity(in frame, shadowConfig);
        var shadowLayersResized = state.Shadows.Upload(in frame, shadowConfig);
        var layerCount = shadowConfig.LayerCount;
        EnsureShadowCameraBuffers(state, in frame, layerCount);
        if (atlasResized || state.ShadowDrawBindGroups.Length != layerCount) {
            EnsureShadowDrawBindGroups(state, in frame, layerCount);
        }

        for (var layer = 0; layer < layerCount; layer++) {
            Wgpu.WriteBuffer(
                frame.Queue.GetWgpu<WGPUQueue>(),
                state.ShadowCameraBuffers[layer].GetWgpu<WGPUBuffer>(),
                0,
                [new CameraUniformData(state.Shadows.LayerViewProj(layer), float4.zero)]);
        }

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
        if (lightsResized || buffersResized || atlasResized || shadowLayersResized || !state.ForwardLightingBindGroup.IsValid) {
            EnsureForwardLightingBindGroup(state, in frame);
        }

        PrepareIbl(state, in frame, extracted);
    }

    public void PrepareIbl(PbrViewState state, in GpuFrame frame, PbrExtractedView extracted)
    {
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
            forwardPipeline.IblBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
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

    public void EncodeShadowLayer(
        PbrViewState state,
        IReadOnlyList<PbrDrawItem> items,
        int layer,
        WgpuHandle<WGPURenderPassEncoder> renderPass)
    {
        if (items.Count == 0) {
            return;
        }

        Wgpu.SetRenderPipeline(renderPass, shadowDepthPipeline.RenderPipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, state.ShadowDrawBindGroups[layer].GetWgpu<WGPUBindGroup>());
        foreach (var item in items) {
            var mesh = state.Meshes[item.Mesh];
            Wgpu.SetVertexBuffer(renderPass, 0, mesh.VertexBuffer.GetWgpu<WGPUBuffer>());
            Wgpu.SetIndexBuffer(renderPass, mesh.IndexBuffer.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
            Wgpu.DrawIndexed(renderPass, mesh.IndexCount, instanceCount: 1, firstInstance: (uint)item.InstanceIndex);
        }
    }

    private void EnsureShadowCameraBuffers(PbrViewState state, in GpuFrame frame, int layerCount)
    {
        if (state.ShadowCameraBuffers.Length == layerCount) {
            return;
        }
        foreach (var existing in state.ShadowCameraBuffers) {
            if (existing.IsValid) {
                existing.Destroy();
            }
        }
        var buffers = new Entity[layerCount];
        for (var layer = 0; layer < layerCount; layer++) {
            buffers[layer] = frame.ResourceWorld.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
                NextInChain = null,
                Label = default,
                Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst,
                Size = CameraUniformData.Stride,
                MappedAtCreation = 0
            });
        }
        state.ShadowCameraBuffers = buffers;
    }

    private void EnsureShadowDrawBindGroups(PbrViewState state, in GpuFrame frame, int layerCount)
    {
        foreach (var existing in state.ShadowDrawBindGroups) {
            if (existing.IsValid) {
                existing.Destroy();
            }
        }

        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        var bindGroups = new Entity[layerCount];
        for (var layer = 0; layer < layerCount; layer++) {
            bindGroups[layer] = frame.ResourceWorld.OwnWgpu(PbrObjectBindGroupLayout.CreateBindGroup(
                deviceHandle,
                shadowDepthPipeline.BindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
                state.ShadowCameraBuffers[layer].GetWgpu<WGPUBuffer>(),
                state.Instances.IsValid ? state.Instances.Buffer.GetWgpu<WGPUBuffer>() : default,
                state.Instances.Capacity));
        }
        state.ShadowDrawBindGroups = bindGroups;
    }

    private void EnsureCullingBindGroup(PbrViewState state, in GpuFrame frame)
    {
        if (state.CullingBindGroup.IsValid) {
            state.CullingBindGroup.Destroy();
        }

        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        state.CullingBindGroup = frame.ResourceWorld.OwnWgpu(ClusterCullingBindGroupLayout.CreateBindGroup(
            deviceHandle,
            cullingPipeline.BindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            state.ClusterBuffers.ConfigBuffer.GetWgpu<WGPUBuffer>(),
            state.Lights.ClusteredBuffer.GetWgpu<WGPUBuffer>(), state.Lights.ClusteredCapacity,
            state.ClusterBuffers.LightGridBuffer.GetWgpu<WGPUBuffer>(), state.ClusterBuffers.LightGridSize,
            state.ClusterBuffers.LightIndexListBuffer.GetWgpu<WGPUBuffer>(), state.ClusterBuffers.LightIndexListCapacity));
    }

    private void EnsureForwardLightingBindGroup(PbrViewState state, in GpuFrame frame)
    {
        if (state.ForwardLightingBindGroup.IsValid) {
            state.ForwardLightingBindGroup.Destroy();
        }

        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        state.ForwardLightingBindGroup = frame.ResourceWorld.OwnWgpu(PbrLightingBindGroupLayout.CreateBindGroup(
            deviceHandle,
            forwardPipeline.LightingBindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
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

    public void EncodeDepthPrepass(
        PbrViewState state,
        IReadOnlyList<PbrDrawItem> items,
        WgpuHandle<WGPURenderPassEncoder> renderPass) =>
        Encode(
            state, items, renderPass, depthPipeline.RenderPipeline,
            state.DepthBindGroup, default, default);

    public void EncodeForwardPbr(
        PbrViewState state,
        IReadOnlyList<PbrDrawItem> items,
        WgpuHandle<WGPURenderPassEncoder> renderPass) =>
        Encode(
            state, items, renderPass, forwardPipeline.RenderPipeline,
            state.ForwardBindGroup, state.ForwardLightingBindGroup, state.IblBindGroup);

    private static void Encode(
        PbrViewState state,
        IReadOnlyList<PbrDrawItem> items,
        WgpuHandle<WGPURenderPassEncoder> renderPass,
        Entity pipeline, Entity bindGroup, Entity lightingBindGroup, Entity iblBindGroup)
    {
        if (items.Count == 0) {
            return;
        }

        Wgpu.SetRenderPipeline(renderPass, pipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(renderPass, 0, bindGroup.GetWgpu<WGPUBindGroup>());
        if (lightingBindGroup.IsValid) {
            Wgpu.SetBindGroup(renderPass, 1, lightingBindGroup.GetWgpu<WGPUBindGroup>());
        }
        if (iblBindGroup.IsValid) {
            Wgpu.SetBindGroup(renderPass, 2, iblBindGroup.GetWgpu<WGPUBindGroup>());
        }

        foreach (var item in items) {
            var mesh = state.Meshes[item.Mesh];
            Wgpu.SetVertexBuffer(renderPass, 0, mesh.VertexBuffer.GetWgpu<WGPUBuffer>());
            Wgpu.SetIndexBuffer(renderPass, mesh.IndexBuffer.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
            Wgpu.DrawIndexed(renderPass, mesh.IndexCount, instanceCount: 1, firstInstance: (uint)item.InstanceIndex);
        }
    }

    private void EnsureBindGroups(PbrViewState state, in GpuFrame frame)
    {
        if (state.DepthBindGroup.IsValid) {
            state.DepthBindGroup.Destroy();
        }
        if (state.ForwardBindGroup.IsValid) {
            state.ForwardBindGroup.Destroy();
        }

        var deviceHandle = frame.Device.GetWgpu<WGPUDevice>();
        var cameraBuffer = state.CameraUniforms.Buffer.GetWgpu<WGPUBuffer>();
        var instanceBuffer = state.Instances.Buffer.GetWgpu<WGPUBuffer>();

        state.DepthBindGroup = frame.ResourceWorld.OwnWgpu(PbrObjectBindGroupLayout.CreateBindGroup(
            deviceHandle,
            depthPipeline.BindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            cameraBuffer, instanceBuffer, state.Instances.Capacity));
        state.ForwardBindGroup = frame.ResourceWorld.OwnWgpu(PbrObjectBindGroupLayout.CreateBindGroup(
            deviceHandle,
            forwardPipeline.BindGroupLayout.GetWgpu<WGPUBindGroupLayout>(),
            cameraBuffer, instanceBuffer, state.Instances.Capacity));

        if (state.ShadowDrawBindGroups.Length > 0) {
            EnsureShadowDrawBindGroups(state, in frame, state.ShadowDrawBindGroups.Length);
        }
    }
}
