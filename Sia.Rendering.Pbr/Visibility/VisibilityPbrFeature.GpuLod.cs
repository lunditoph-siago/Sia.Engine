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

    private static LodGpu CreateLodGpu(World world, WgpuHandle<WGPUDevice> device, WgpuHandle<WGPUQueue> queue,
        MeshPatchTree tree, uint instances, VisibilityLodSettings settings, WGPULimits limits, List<Entity> acquired, bool enableTiming)
    {
        var count = checked((uint)tree.Nodes.Length * instances);
        if (count > (ulong)limits.MaxComputeWorkgroupsPerDimension * limits.MaxComputeWorkgroupsPerDimension) {
            throw new ArgumentException("The patch/instance count exceeds compute dispatch limits.", nameof(instances));
        }
        var source = tree.Nodes.Span;
        var nodes = new PatchGpu[source.Length];
        uint maxChildren = 0;
        for (var i = 0; i < source.Length; i++) {
            var node = source[i];
            maxChildren = System.Math.Max(maxChildren, (uint)node.ChildCount);
            uint childMeshlets = 0, childTriangles = 0;
            for (var child = node.ChildOffset; child < node.ChildOffset + node.ChildCount; child++) {
                childMeshlets = checked(childMeshlets + (uint)source[child].MeshletCount);
                childTriangles = checked(childTriangles + (uint)source[child].TriangleCount);
            }
            nodes[i] = new(new float4(node.Bounds.Min, node.EstimatedSpatialError), new float4(node.Bounds.Max, 0),
                new uint4((uint)node.ChildOffset, (uint)node.ChildCount, childMeshlets, childTriangles),
                new uint4((uint)node.MeshletCount, (uint)node.TriangleOffset, (uint)node.TriangleCount, 0));
        }
        var patches = Upload<PatchGpu>(world, device, queue, nodes, WGPUBufferUsage.Storage, limits, acquired);
        var parameters = Upload<LodParamsGpu>(world, device, queue, [new(
            new uint4((uint)source.Length, (uint)tree.RootCount, instances, limits.MaxComputeWorkgroupsPerDimension),
            new uint4((uint)settings.Budget.MaxPatches, (uint)settings.Budget.MaxMeshlets, (uint)settings.Budget.MaxTriangles,
                BitConverter.SingleToUInt32Bits(settings.TargetPixelError == 0 ? 0 : settings.TargetPixelError)),
            new uint4((uint)settings.Budget.MaxRefinementCandidates, (uint)settings.Budget.MaxRefinementNodes, 0, 0))],
            WGPUBufferUsage.Uniform, limits, acquired);
        var layout = Layout(world, device, [
            BufferLayout(0, WGPUBufferBindingType.Uniform, 128, WGPUShaderStage.Compute),
            BufferLayout(1, WGPUBufferBindingType.Uniform, 48, WGPUShaderStage.Compute),
            BufferLayout(2, WGPUBufferBindingType.ReadOnlyStorage, 64, WGPUShaderStage.Compute),
            BufferLayout(3, WGPUBufferBindingType.ReadOnlyStorage, 176, WGPUShaderStage.Compute),
            BufferLayout(4, WGPUBufferBindingType.Storage, 16, WGPUShaderStage.Compute),
            BufferLayout(5, WGPUBufferBindingType.Storage, 4, WGPUShaderStage.Compute),
            BufferLayout(6, WGPUBufferBindingType.Storage, 80, WGPUShaderStage.Compute),
            BufferLayout(7, WGPUBufferBindingType.Storage, 16, WGPUShaderStage.Compute)
        ], acquired);
        var shader = Own(world, Wgpu.CreateWgslShaderModule(device, PbrShaderSource.LoadVisibilityLod(), "visibility-lod"), acquired);
        var pipelineLayout = PipelineLayout(world, device, [layout], acquired);
        var occlusion = CreateOcclusionGpu(world, device, layout, pipelineLayout, shader, acquired);
        var roots = (uint)tree.RootCount * instances;
        var refinedNodes = System.Math.Min((ulong)count - roots,
            System.Math.Min((uint)settings.Budget.MaxRefinementNodes, (ulong)maxChildren * (uint)settings.Budget.MaxRefinementCandidates));
        var heapCount = System.Math.Max(checked(roots + (uint)refinedNodes), CompactionCapacity(count));
        return new(patches, parameters, layout,
            ComputePipeline(world, device, shader, pipelineLayout, "project", acquired),
            ComputePipeline(world, device, shader, pipelineLayout, "select_cut", acquired),
            ComputePipeline(world, device, shader, pipelineLayout, "emit_work", acquired),
            count, heapCount, limits.MaxComputeWorkgroupsPerDimension, occlusion,
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
        var count = System.Math.Max(1u, lod.Count);
        var state = Allocate(_world, _device.GetWgpu<WGPUDevice>(), count * 16ul,
            WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc, limits, acquired);
        var heap = Allocate(_world, _device.GetWgpu<WGPUDevice>(), lod.HeapCount * 4ul, WGPUBufferUsage.Storage, limits, acquired);
        var group = Own(_world, BindGroup(lod.Layout, [BufferEntry(0, camera), BufferEntry(1, lod.Parameters),
            BufferEntry(2, lod.Patches), BufferEntry(3, _geometry[4]), BufferEntry(4, state), BufferEntry(5, heap),
            BufferEntry(6, indirect), BufferEntry(7, work)]), acquired);
        return new(state, heap, group, CreateCompactionView(lod, state, heap, indirect, limits, acquired));
    }

    private static void BuildLodGraph(ref RenderGraphBuildContext graph, ViewState view, LodGpu lod)
    {
        var state = view.Lod!.Value;
        ImportBuffer(ref graph, s_PatchKey, lod.Patches, RenderGraphBufferUsage.Storage);
        ImportBuffer(ref graph, s_LodParamsKey, lod.Parameters, RenderGraphBufferUsage.Uniform);
        ImportBuffer(ref graph, s_LodStateKey, state.State, RenderGraphBufferUsage.Storage | RenderGraphBufferUsage.CopySource);
        ImportBuffer(ref graph, s_LodHeapKey, state.Heap, RenderGraphBufferUsage.Storage);
        graph.UseComputePass(new("visibility-lod-project"), "visibility-lod-project", declaration => declaration
            .Read(s_CameraKey, RenderGraphBufferUsage.Uniform).Read(s_LodParamsKey, RenderGraphBufferUsage.Uniform)
            .Read(s_PatchKey, RenderGraphBufferUsage.Storage).Read(s_GeometryKeys[4], RenderGraphBufferUsage.Storage)
            .Write(s_LodStateKey, RenderGraphBufferUsage.Storage), view.ProjectLod);
        graph.UseComputePass(new("visibility-lod-select"), "visibility-lod-select", declaration => declaration
            .Read(s_CameraKey, RenderGraphBufferUsage.Uniform).Read(s_GeometryKeys[4], RenderGraphBufferUsage.Storage)
            .Read(s_LodParamsKey, RenderGraphBufferUsage.Uniform).Read(s_PatchKey, RenderGraphBufferUsage.Storage)
            .ReadWrite(s_LodStateKey, RenderGraphBufferUsage.Storage).Write(s_LodHeapKey, RenderGraphBufferUsage.Storage)
            .Write(s_IndirectKey, RenderGraphBufferUsage.Storage), view.SelectLod);
        BuildMainOcclusionGraph(ref graph, view);
        graph.UseComputePass(new("visibility-lod-emit"), "visibility-lod-emit", declaration => declaration
            .Read(s_LodParamsKey, RenderGraphBufferUsage.Uniform).Read(s_PatchKey, RenderGraphBufferUsage.Storage)
            .Read(s_LodStateKey, RenderGraphBufferUsage.Storage).Write(s_WorkKey, RenderGraphBufferUsage.Storage), view.EmitLod);
    }

    private sealed partial class ViewState
    {
        public void ProjectLod(WgpuReactiveRenderGraphPassContext context) =>
            DispatchLod(context, Owner._gpuLod!.Value.Project, (Owner._gpuLod.Value.Count + 63) / 64);

        public void SelectLod(WgpuReactiveRenderGraphPassContext context) => DispatchLod(context, Owner._gpuLod!.Value.Select, 1);

        public void EmitLod(WgpuReactiveRenderGraphPassContext context) =>
            DispatchLod(context, Owner._gpuLod!.Value.Emit, Owner._gpuLod.Value.Count);

        private void DispatchLod(WgpuReactiveRenderGraphPassContext context, Entity pipeline, uint count, Entity? extraGroup = null)
        {
            var dimension = Owner._gpuLod!.Value.DispatchDimension;
            count = System.Math.Max(1u, count);
            var pass = BeginCompute(context);
            try {
                Wgpu.SetComputePipeline(pass, pipeline.GetWgpu<WGPUComputePipeline>());
                Wgpu.SetBindGroup(pass, 0, Lod!.Value.Group.GetWgpu<WGPUBindGroup>());
                if (extraGroup is { } group) { Wgpu.SetBindGroup(pass, 1, group.GetWgpu<WGPUBindGroup>()); }
                Wgpu.DispatchWorkgroups(pass, System.Math.Min(count, dimension), (count + dimension - 1) / dimension);
            }
            finally { Wgpu.EndComputePass(pass); Wgpu.Release(ref pass); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PatchGpu(float4 MinimumError, float4 Maximum, uint4 Children, uint4 Geometry);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct LodParamsGpu(uint4 Counts, uint4 Budget, uint4 Traversal);

    private readonly record struct LodGpu(Entity Patches, Entity Parameters, Entity Layout, Entity Project, Entity Select,
        Entity Emit, uint Count, uint HeapCount, uint DispatchDimension, OcclusionGpu Occlusion, CompactionGpu Compaction, bool EnableTiming);

    private readonly record struct LodViewGpu(Entity State, Entity Heap, Entity Group, CompactionViewGpu Compaction);
}
