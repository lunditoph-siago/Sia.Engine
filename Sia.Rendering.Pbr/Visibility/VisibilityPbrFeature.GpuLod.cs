using System.Runtime.InteropServices;
using System.Text;
using Sia;
using Sia.Engine.Mesh;
using Sia.Graphics.Reactive;
using Sia.Math;
using Sia.RenderGraph;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class VisibilityPbrFeature
{
    private static readonly RenderGraphBufferKey s_PatchKey = new("visibility-patches");
    private static readonly RenderGraphBufferKey s_LodParamsKey = new("visibility-lod-params");
    private static readonly RenderGraphBufferKey s_LodStateKey = new("visibility-lod-state");
    private static readonly RenderGraphBufferKey s_LodHeapKey = new("visibility-lod-heap");
    private static readonly RenderGraphBufferKey s_LodDispatchKey = new("visibility-lod-dispatch");

    private static LodGpu CreateLodGpu(World world, WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue,
        SceneLodData scene, VisibilityLodSettings settings, WGPULimits limits, List<Entity> acquired, bool enableTiming)
    {
        var instances = (uint)scene.InstanceRoots.Length;
        if (System.Math.Max(scene.StateCapacity, (uint)scene.InstanceCapacity) > (ulong)limits.MaxComputeWorkgroupsPerDimension * limits.MaxComputeWorkgroupsPerDimension) {
            throw new ArgumentException("The scene exceeds compute dispatch limits.", nameof(scene));
        }
        var patches = Upload<PatchGpu>(world, device, queue, scene.Patches, WGPUBufferUsage.Storage, limits, acquired);
        var parameters = Upload<LodParamsGpu>(world, device, queue, [new(
            new uint4((uint)scene.Patches.Length, scene.RootCost.x, instances, limits.MaxComputeWorkgroupsPerDimension),
            new uint4((uint)settings.Budget.MaxPatches, (uint)settings.Budget.MaxMeshlets, (uint)settings.Budget.MaxTriangles,
                BitConverter.SingleToUInt32Bits(settings.TargetPixelError == 0 ? 0 : settings.TargetPixelError)),
            new uint4((uint)settings.Budget.MaxRefinementCandidates, (uint)settings.Budget.MaxRefinementNodes, 0, 0))],
            WGPUBufferUsage.Uniform, limits, acquired);
        var layout = Layout(world, device, [
            BufferLayout(0, WGPUBufferBindingType.Uniform, 128, WGPUShaderStage.Compute),
            BufferLayout(1, WGPUBufferBindingType.Uniform, 48, WGPUShaderStage.Compute),
            BufferLayout(2, WGPUBufferBindingType.ReadOnlyStorage, 64, WGPUShaderStage.Compute),
            BufferLayout(3, WGPUBufferBindingType.ReadOnlyStorage, 192, WGPUShaderStage.Compute),
            BufferLayout(4, WGPUBufferBindingType.Storage, 20, WGPUShaderStage.Compute),
            BufferLayout(5, WGPUBufferBindingType.Storage, 4, WGPUShaderStage.Compute),
            BufferLayout(6, WGPUBufferBindingType.Storage, 80, WGPUShaderStage.Compute),
            BufferLayout(7, WGPUBufferBindingType.Storage, 8, WGPUShaderStage.Compute)
        ], acquired);
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityLod(), "visibility-lod"), acquired);
        var pipelineLayout = PipelineLayout(world, device, [layout], acquired);
        var dispatchLayout = Layout(world, device, [BufferLayout(2, WGPUBufferBindingType.Storage, 72, WGPUShaderStage.Compute)], acquired);
        var selectLayout = PipelineLayout(world, device, [layout, dispatchLayout], acquired);
        var occlusion = CreateOcclusionGpu(world, device, layout, pipelineLayout, shader, acquired);
        return new(patches, parameters, layout, dispatchLayout,
            ComputePipeline(world, device, shader, pipelineLayout, "project", acquired),
            ComputePipeline(world, device, shader, selectLayout, "select_cut", acquired),
            ComputePipeline(world, device, shader, pipelineLayout, "emit_work", acquired),
            scene.StateCapacity, limits.MaxComputeWorkgroupsPerDimension, occlusion,
            CreateCompactionGpu(world, device, acquired), enableTiming);
    }

    private static unsafe Entity ComputePipeline(World world, WgpuHandle<WGPUDevice> device, Entity shader,
        Entity layout, string entry, List<Entity> acquired)
    {
        var bytes = Encoding.UTF8.GetBytes(entry);
        fixed (byte* pointer = bytes) {
            var descriptor = WGPUComputePipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)layout.GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Compute = WGPUComputeState.Default;
            descriptor.Compute.Module = (WGPUShaderModule*)shader.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            descriptor.Compute.EntryPoint = new WGPUStringView { Data = pointer, Length = (nuint)bytes.Length };
            return Own(world, Wgpu.CreateComputePipeline(device, descriptor), acquired);
        }
    }

    private static Entity Allocate(World world, WgpuHandle<WGPUDevice> device, ulong size,
        WGPUBufferUsage usage, WGPULimits limits, List<Entity> acquired)
    {
        if (size > limits.MaxBufferSize || ((usage & WGPUBufferUsage.Storage) != 0 && size > limits.MaxStorageBufferBindingSize)) {
            throw new ArgumentException("Visibility scratch storage exceeds the device binding limit.");
        }
        return Own(world, Wgpu.CreateBuffer(device, new WGPUBufferDescriptor { Size = size, Usage = usage }), acquired);
    }

    private LodViewGpu CreateLodView(LodGpu lod, Entity camera, Entity work, Entity indirect, WGPULimits limits, List<Entity> acquired)
    {
        var state = Allocate(_world, _device.GetWgpu<WGPUDevice>(), lod.Capacity * 20ul,
            WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc, limits, acquired);
        var heap = Allocate(_world, _device.GetWgpu<WGPUDevice>(), lod.Capacity * 4ul, WGPUBufferUsage.Storage, limits, acquired);
        var dispatch = Allocate(_world, _device.GetWgpu<WGPUDevice>(), 72,
            WGPUBufferUsage.Storage | WGPUBufferUsage.Indirect | WGPUBufferUsage.CopySrc, limits, acquired);
        var group = Own(_world, BindGroup(lod.Layout, [BufferEntry(0, camera), BufferEntry(1, lod.Parameters),
            BufferEntry(2, lod.Patches), BufferEntry(3, _geometry[3]), BufferEntry(4, state), BufferEntry(5, heap),
            BufferEntry(6, indirect), BufferEntry(7, work)]), acquired);
        var dispatchGroup = Own(_world, BindGroup(lod.DispatchLayout, [BufferEntry(2, dispatch)]), acquired);
        return new(state, heap, dispatch, group, dispatchGroup, CreateCompactionView(lod, state, heap, indirect, limits, acquired));
    }

    private static void BuildLodGraph(ref RenderGraphBuildContext graph, ViewState view, LodGpu lod)
    {
        var state = view.Lod!.Value;
        ImportBuffer(ref graph, s_PatchKey, lod.Patches, RenderGraphBufferUsage.Storage);
        ImportBuffer(ref graph, s_LodParamsKey, lod.Parameters, RenderGraphBufferUsage.Uniform);
        ImportBuffer(ref graph, s_LodStateKey, state.State, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.CopySource);
        ImportBuffer(ref graph, s_LodHeapKey, state.Heap, RenderGraphBufferUsage.Storage);
        ImportBuffer(ref graph, s_LodDispatchKey, state.Dispatch, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.Indirect | RenderGraphBufferUsage.CopySource);
        graph.UseComputePass(new("visibility-lod-project"), "visibility-lod-project", declaration => declaration
            .Read(s_CameraKey, RenderGraphBufferUsage.Uniform).Read(s_LodParamsKey, RenderGraphBufferUsage.Uniform)
            .Read(s_PatchKey, RenderGraphBufferUsage.Storage).Read(s_GeometryKeys[3], RenderGraphBufferUsage.Storage)
            .Write(s_LodStateKey, RenderGraphBufferUsage.Storage), view.ProjectLod);
        graph.UseComputePass(new("visibility-lod-select"), "visibility-lod-select", declaration => declaration
            .Read(s_CameraKey, RenderGraphBufferUsage.Uniform).Read(s_GeometryKeys[3], RenderGraphBufferUsage.Storage)
            .Read(s_LodParamsKey, RenderGraphBufferUsage.Uniform).Read(s_PatchKey, RenderGraphBufferUsage.Storage)
            .ReadWrite(s_LodStateKey, RenderGraphBufferUsage.Storage).Write(s_LodHeapKey, RenderGraphBufferUsage.Storage)
            .Write(s_LodDispatchKey, RenderGraphBufferUsage.Storage)
            .Write(s_IndirectKey, RenderGraphBufferUsage.Storage), view.SelectLod);
        BuildMainOcclusionGraph(ref graph, view);
        graph.UseComputePass(new("visibility-lod-emit"), "visibility-lod-emit", declaration => declaration
            .Read(s_LodParamsKey, RenderGraphBufferUsage.Uniform).Read(s_PatchKey, RenderGraphBufferUsage.Storage)
            .Read(s_LodStateKey, RenderGraphBufferUsage.Storage).Read(s_IndirectKey, RenderGraphBufferUsage.Storage)
            .Read(s_LodDispatchKey, RenderGraphBufferUsage.Indirect)
            .Write(s_WorkKey, RenderGraphBufferUsage.Storage), view.EmitLod);
    }

    private sealed partial class ViewState
    {
        public void ProjectLod(WgpuReactiveRenderGraphPassContext context) =>
            DispatchLod(context, Owner._gpuLod!.Value.Project, Owner.InstanceCount);

        public void SelectLod(WgpuReactiveRenderGraphPassContext context) =>
            DispatchLod(context, Owner._gpuLod!.Value.Select, 1, Lod!.Value.DispatchGroup);

        public void EmitLod(WgpuReactiveRenderGraphPassContext context) =>
            DispatchLod(context, Owner._gpuLod!.Value.Emit, 0, indirectOffset: 12);

        private void DispatchLod(WgpuReactiveRenderGraphPassContext context, Entity pipeline, uint count, Entity? extraGroup = null, ulong? indirectOffset = null)
        {
            var dimension = Owner._gpuLod!.Value.DispatchDimension;
            count = System.Math.Max(1u, count);
            var pass = BeginCompute(context);
            try {
                Wgpu.SetComputePipeline(pass, pipeline.GetWgpu<WGPUComputePipeline>());
                Wgpu.SetBindGroup(pass, 0, Lod!.Value.Group.GetWgpu<WGPUBindGroup>());
                if (extraGroup is { } group) { Wgpu.SetBindGroup(pass, 1, group.GetWgpu<WGPUBindGroup>()); }
                if (indirectOffset is { } offset) { Wgpu.DispatchWorkgroupsIndirect(pass, Lod.Value.Dispatch.GetWgpu<WGPUBuffer>(), offset); }
                else { Wgpu.DispatchWorkgroups(pass, System.Math.Min(count, dimension), (count + dimension - 1) / dimension); }
            }
            finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PatchGpu(float4 MinimumError, float4 Maximum, uint4 Children, uint4 Geometry);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct LodParamsGpu(uint4 Counts, uint4 Budget, uint4 Traversal);

    private readonly record struct LodGpu(Entity Patches, Entity Parameters, Entity Layout, Entity DispatchLayout, Entity Project, Entity Select,
        Entity Emit, uint Capacity, uint DispatchDimension, OcclusionGpu Occlusion, CompactionGpu Compaction, bool EnableTiming);

    private readonly record struct LodViewGpu(Entity State, Entity Heap, Entity Dispatch, Entity Group, Entity DispatchGroup, CompactionViewGpu Compaction);
}
