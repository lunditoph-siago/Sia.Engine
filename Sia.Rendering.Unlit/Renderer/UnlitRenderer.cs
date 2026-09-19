using Sia;
using Sia.Engine.Camera;
using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Unlit;

public sealed class UnlitRenderer
{
    private readonly UnlitPipeline _pipeline;

    public UnlitRenderer(UnlitPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
    }

    internal UnlitExtractedView Extract(UnlitViewState state, in GpuFrame frame, Entity camera)
    {
        var matrices = camera.Get<CameraMatrices>();
        var cache = frame.MainWorld.AcquireAddon<UnlitRenderCache>();
        cache.Refresh();
        if (state.SceneVersion == cache.Version && state.Extracted is { } previous
            && previous.ViewProjection.Equals(matrices.ViewProj)) return previous;
        var visible = cache.Cull(matrices.Frustum);
        var items = new UnlitDrawItem[visible.Count];
        for (var i = 0; i < items.Length; i++) {
            var index = visible[i];
            items[i] = new(cache.MeshHandles[index], index);
        }
        state.SceneVersion = cache.Version;
        return state.Extracted = new(matrices.ViewProj, cache.Data, items);
    }

    internal void Prepare(UnlitViewState state, in GpuFrame frame, UnlitExtractedView extracted)
    {
        var meshes = frame.ResourceWorld.AcquireAddon<MeshGpuStore>();
        var registry = frame.ResourceWorld.AcquireAddon<MeshRegistry>();
        state.Meshes.Clear();
        foreach (var item in extracted.Items) {
            if (!state.Meshes.ContainsKey(item.Mesh)) {
                state.Meshes.Add(item.Mesh, meshes.GetOrUpload(in frame, registry, item.Mesh));
            }
        }
        if (!state.CameraBuffer.IsValid) {
            state.CameraBuffer = frame.ResourceWorld.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
                Size = 64,
                Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst
            });
        }
        if (state.Instances.Upload(in frame, extracted.Instances.Span) || !state.BindGroup.IsValid) {
            var next = frame.ResourceWorld.OwnWgpu(_pipeline.CreateBindGroup(frame.Device.GetWgpu<WGPUDevice>(), state));
            if (state.BindGroup.IsValid) state.BindGroup.Destroy();
            state.BindGroup = next;
        }
        Wgpu.WriteBuffer(frame.Queue.GetWgpu<WGPUQueue>(),
            state.CameraBuffer.GetWgpu<WGPUBuffer>(), 0, [extracted.ViewProjection]);
    }

    internal void Encode(
        UnlitViewState state,
        RenderPhase<UnlitDrawItem> phase,
        WgpuHandle<WGPURenderPassEncoder> pass)
    {
        Wgpu.SetRenderPipeline(pass, _pipeline.Pipeline.GetWgpu<WGPURenderPipeline>());
        Wgpu.SetBindGroup(pass, 0, state.BindGroup.GetWgpu<WGPUBindGroup>());
        foreach (var item in phase.Items) {
            var mesh = state.Meshes[item.Mesh];
            Wgpu.SetVertexBuffer(pass, 0, mesh.VertexBuffer.GetWgpu<WGPUBuffer>());
            Wgpu.SetIndexBuffer(pass, mesh.IndexBuffer.GetWgpu<WGPUBuffer>(), WGPUIndexFormat.Uint32);
            Wgpu.DrawIndexed(pass, mesh.IndexCount, instanceCount: 1, firstInstance: (uint)item.InstanceIndex);
        }
    }
}
