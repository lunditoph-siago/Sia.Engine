using Sia;
using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private FusedLightingGpu? _fusedLighting;
    internal bool FusedLighting => _fusedLighting is not null;
    private sealed record FusedLightingGpu(Entity Layout, Entity LightLayout, Entity Pipeline);

    internal void PrepareFusedLighting()
    {
        if (_fusedLighting is not null) return;
        if (_prepared) throw new InvalidOperationException("Scene shading must be selected before preparation.");
        var acquired = new List<Entity>();
        var device = _device.GetWgpu<WGPUDevice>();
        try {
            var textures = CreateResolveLayout(_world, device, acquired, fused: true);
            var lights = Own(_world, PbrLightingBindGroupLayout.Create(device, unclustered: true), acquired);
            var ibl = Own(_world, PbrIblBindGroupLayout.Create(device), acquired);
            var layout = PipelineLayout(_world, device, [_geometryLayout, textures, lights, ibl], acquired);
            var shader = Own(_world, Wgpu.CreateWgslShaderModule(device,
                PbrShaderSource.LoadFusedLighting(_worldSpaceGeometry), "visibility-fused-lighting"), acquired);
            _fusedLighting = new(textures, lights, ComputePipeline(_world, device, shader, layout, "resolve", acquired));
        } catch {
            for (var i = acquired.Count - 1; i >= 0; i--) acquired[i].Destroy();
            throw;
        }
    }

    internal void BuildFusedLighting(ref RenderGraphBuildContext graph, in RenderFeatureContext<RenderFrameContext> context,
        PbrViewState lighting, RenderGraphTextureKey color)
    {
        var view = context.View.PersistentResources.GetRequired<ViewState>();
        view.SceneLighting = lighting;
        view.FusedTarget = color;
        graph.UseComputePass(new("visibility-resolve"), "visibility-resolve", view.DeclareResolve, view.Resolve);
    }

    private sealed partial class ViewState
    {
        internal PbrViewState? SceneLighting;
        internal RenderGraphTextureKey FusedTarget;
        internal Entity FusedGroup;
        private Entity _fusedSourceGroup;

        private void PrepareFusedGroup()
        {
            if (Owner._fusedLighting is not { } fused) return;
            var s = SceneLighting!;
            if (FusedGroup.IsValid && _fusedSourceGroup == s.LightingBindGroup) return;
            var next = Owner._world.OwnWgpu(PbrLightingBindGroupLayout.CreateBindGroup(
                Owner._device.GetWgpu<WGPUDevice>(), fused.LightLayout.GetWgpu<WGPUBindGroupLayout>(),
                s.ClusterBuffers.ConfigBuffer.GetWgpu<WGPUBuffer>(), s.Lights.ClusteredBuffer.GetWgpu<WGPUBuffer>(), s.Lights.ClusteredCapacity,
                s.ClusterBuffers.LightGridBuffer.GetWgpu<WGPUBuffer>(), s.ClusterBuffers.LightGridSize,
                s.ClusterBuffers.LightIndexListBuffer.GetWgpu<WGPUBuffer>(), s.ClusterBuffers.LightIndexListCapacity,
                s.Lights.DirectionalBuffer.GetWgpu<WGPUBuffer>(), s.ShadowAtlas.SamplingView.GetWgpu<WGPUTextureView>(),
                s.ShadowAtlas.Sampler.GetWgpu<WGPUSampler>(), s.Shadows.LayerBuffer.GetWgpu<WGPUBuffer>(), s.Shadows.LayerBufferCapacity,
                s.Shadows.ConfigBuffer.GetWgpu<WGPUBuffer>(), unclustered: true));
            if (FusedGroup.IsValid) FusedGroup.Destroy();
            FusedGroup = next;
            _fusedSourceGroup = s.LightingBindGroup;
        }
    }
}
