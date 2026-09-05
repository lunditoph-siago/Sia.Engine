using Sia;
using Sia.Engine.Camera;
using Sia.Engine.Mesh;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Unlit;

public sealed class UnlitRenderer
{
    private static readonly IEntityMatcher _matcher = Matchers.Of<
        global::Sia.Engine.Mesh.Mesh, UnlitMaterial, MeshRenderer, GlobalTransform, WorldBounds>();
    private readonly UnlitPipeline _pipeline;

    public UnlitRenderer(UnlitPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
    }

    internal UnlitExtractedView Extract(in GpuFrame frame, Entity camera)
    {
        var matrices = camera.Get<CameraMatrices>();
        var instances = new List<UnlitInstance>();
        var items = new List<UnlitDrawItem>();
        frame.MainWorld.Query(_matcher, entity => {
            if (!matrices.Frustum.Intersects(entity.Get<WorldBounds>().World)) {
                return;
            }
            var transform = entity.Get<GlobalTransform>().Affine;
            var normal = new float4x4(math.transpose(math.inverse(transform.RotationScale)), float3.zero);
            var mesh = entity.Get<global::Sia.Engine.Mesh.Mesh>().Handle;
            items.Add(new UnlitDrawItem(mesh, instances.Count));
            instances.Add(new UnlitInstance(transform, normal, entity.Get<UnlitMaterial>().Color));
        });
        return new UnlitExtractedView(matrices.ViewProj, [.. instances], [.. items]);
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
        var required = (ulong)System.Math.Max(1, extracted.Instances.Length) * UnlitInstance.Stride;
        if (state.InstanceCapacity < required) {
            if (state.BindGroup.IsValid) {
                state.BindGroup.Destroy();
            }
            if (state.InstanceBuffer.IsValid) {
                state.InstanceBuffer.Destroy();
            }
            state.InstanceCapacity = System.Math.Max(required, state.InstanceCapacity * 2);
            state.InstanceBuffer = frame.ResourceWorld.CreateWgpuBuffer(frame.Device, new WGPUBufferDescriptor {
                Size = state.InstanceCapacity,
                Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst
            });
            state.BindGroup = frame.ResourceWorld.OwnWgpu(
                _pipeline.CreateBindGroup(frame.Device.GetWgpu<WGPUDevice>(), state));
        }
        Wgpu.WriteBuffer(frame.Queue.GetWgpu<WGPUQueue>(),
            state.CameraBuffer.GetWgpu<WGPUBuffer>(), 0, [extracted.ViewProjection]);
        Wgpu.WriteBuffer<UnlitInstance>(frame.Queue.GetWgpu<WGPUQueue>(),
            state.InstanceBuffer.GetWgpu<WGPUBuffer>(), 0, extracted.Instances);
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
