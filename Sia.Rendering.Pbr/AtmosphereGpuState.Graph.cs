using Sia;
using Sia.Graphics.Reactive;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class AtmosphereGpuState
{
    internal static readonly RenderGraphTextureKey[] TextureKeys = [new("atmosphere-transmittance"), new("atmosphere-multiscatter"),
        new("atmosphere-sky-view"), new("atmosphere-aerial-radiance"), new("atmosphere-aerial-transmittance")];
    internal static readonly RenderGraphBufferKey IrradianceKey = new("pbr-ibl-sh");

    public static void BuildGraph(ref RenderGraphBuildContext graph, PbrViewState state)
    {
        var atmosphere = state.ActiveAtmosphere is null ? null : state.Atmosphere;
        for (var i = 0; i < _sizes.Length; i++) {
            var size = _sizes[i];
            var descriptor = new RenderGraphTextureDescriptor("atmosphere-lut", RenderGraphTextureFormat.RGBA16Float,
                size.Width, size.Height, size.Depth, dimension: size.Depth > 1 ? RenderGraphTextureDimension.D3 : RenderGraphTextureDimension.D2,
                usage: RenderGraphTextureUsage.TextureBinding | RenderGraphTextureUsage.StorageBinding | RenderGraphTextureUsage.CopySource);
            if (atmosphere is null) {
                descriptor = new RenderGraphTextureDescriptor("inactive-atmosphere", RenderGraphTextureFormat.RGBA16Float,
                    IblEnvironmentGpuStore.PrefilteredResolution, IblEnvironmentGpuStore.PrefilteredResolution, 6,
                    mipLevelCount: IblEnvironmentGpuStore.PrefilteredMipCount,
                    usage: RenderGraphTextureUsage.TextureBinding | RenderGraphTextureUsage.RenderAttachment);
            }
            graph.UseImportedTexture(TextureKeys[i], descriptor);
            graph.BindImportedTexture(TextureKeys[i], (atmosphere is null ? state.Ibl.PrefilteredTexture : atmosphere._textures[i]).GetWgpu<WGPUTexture>());
        }
        for (var i = 0; i < 5; i++) {
            var stage = i;
            var pass = graph.UseState(() => new ComputePass(stage));
            pass.Owner = atmosphere;
            graph.UseComputePass(pass.Key, pass.Name, pass.Declare, pass.Render);
        }
    }

    public static void DeclareSkyRead(RenderGraphPassDeclarationBuilder declaration)
    {
        declaration.Read(TextureKeys[0], RenderGraphTextureUsage.TextureBinding)
            .Read(TextureKeys[1], RenderGraphTextureUsage.TextureBinding)
            .Read(TextureKeys[2], RenderGraphTextureUsage.TextureBinding);
    }

    private sealed class ComputePass(int stage)
    {
        public AtmosphereGpuState? Owner { get; set; }
        public string Name { get; } = "atmosphere-" + stage;
        public RenderGraphPassKey Key { get; } = new("atmosphere-compute-" + stage);

        public void Declare(RenderGraphPassDeclarationBuilder declaration)
        {
            if (Owner is null) { return; }
            if (stage > 0) { declaration.Read(TextureKeys[0], RenderGraphTextureUsage.TextureBinding); }
            if (stage > 1) { declaration.Read(TextureKeys[1], RenderGraphTextureUsage.TextureBinding); }
            if (stage > 2) { declaration.Read(TextureKeys[2], RenderGraphTextureUsage.TextureBinding); }
            if (stage == 4) {
                declaration.Write(IrradianceKey, RenderGraphBufferUsage.Storage);
            } else {
                declaration.Write(TextureKeys[stage], RenderGraphTextureUsage.StorageBinding);
                if (stage == 3) { declaration.Write(TextureKeys[4], RenderGraphTextureUsage.StorageBinding); }
            }
        }

        public void Render(WgpuReactiveRenderGraphPassContext context)
        {
            var owner = Owner;
            if (owner is null) { return; }
            var revision = stage switch { 0 or 1 => owner._mediumRevision, 3 => owner._viewRevision, _ => owner._environmentRevision };
            if (owner._renderedRevisions[stage] == revision) { return; }
            var pass = context.GetOrBeginComputePass();
            Wgpu.SetComputePipeline(pass, owner._pipelines.Compute[stage].GetWgpu<WGPUComputePipeline>());
            Wgpu.SetBindGroup(pass, 0, owner._commonGroups[System.Math.Min(stage, 3)].GetWgpu<WGPUBindGroup>());
            Wgpu.SetBindGroup(pass, 1, owner._outputGroups[stage].GetWgpu<WGPUBindGroup>());
            var dispatch = stage switch { 0 => (32u, 8u, 1u), 1 => (32u, 32u, 1u), 2 => (24u, 14u, 1u), 3 => (8u, 8u, 8u), _ => (1u, 1u, 1u) };
            Wgpu.DispatchWorkgroups(pass, dispatch.Item1, dispatch.Item2, dispatch.Item3);
            Wgpu.EndComputePass(pass);
            Wgpu.Release(ref pass);
            owner._renderedRevisions[stage] = revision;
        }
    }
}
