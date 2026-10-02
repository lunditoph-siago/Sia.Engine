using System.Runtime.InteropServices;
using System.Text;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe class PbrGpuHierarchy : IDisposable
{
    internal const uint WorkBlockTriangles = 16;
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Node(float4 Min, float4 Max, uint4 Links, uint4 Owner);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Configuration(
        float4x4 Projection,
        uint4 Table,
        float4 Screen,
        uint4 Output,
        float4 EyeNear,
        float4 ForwardPixels);

    private readonly GpuResources _gpu;

    public Entity Nodes { get; }
    public Entity Parts { get; }
    public Entity Residency { get; }
    public Entity Instances { get; }
    public Entity Layout { get; }
    public Entity ExpansionLayout { get; }
    public Entity ResetFeedback { get; }
    public Entity ResetDraws { get; }
    public Entity SeedRoots { get; }
    public Entity PrepareEven { get; }
    public Entity PrepareOdd { get; }
    public Entity TraverseEven { get; }
    public Entity TraverseOdd { get; }
    public Entity PrepareExpansion { get; }
    public Entity Expand { get; }

    public uint RootBase { get; }
    public uint RootCount { get; }
    public uint PageCount { get; }
    public uint SingleCapacity { get; }
    public uint DoubleCapacity { get; }
    public uint NodeCapacity { get; }
    public int Levels { get; }

    public ulong Bytes => _gpu.Bytes;

    public PbrGpuHierarchy(in GpuFrame frame, ulong budget, ReadOnlySpan<Node> nodes, ReadOnlySpan<uint4> parts,
        ReadOnlySpan<uint4> mapping, uint rootBase, uint rootCount, uint singleCapacity, uint doubleCapacity,
        uint nodeCapacity, Entity instances)
    {
        _gpu = new(frame, budget);
        Instances = instances;
        (RootBase, RootCount, PageCount, SingleCapacity, DoubleCapacity) =
            (rootBase, rootCount, (uint)mapping.Length, singleCapacity, doubleCapacity);
        NodeCapacity = nodeCapacity;
        var depths = new int[nodes.Length];
        for (var i = 0; i < nodes.Length; i++) {
            var parent = nodes[i].Owner.x;
            if (parent != uint.MaxValue && parent >= i)
                throw new ArgumentException("GPU hierarchy parents must precede their children.", nameof(nodes));
            depths[i] = parent == uint.MaxValue ? 1 : checked(depths[(int)parent] + 1);
            Levels = System.Math.Max(Levels, depths[i]);
        }
        try {
            Nodes = _gpu.Upload(nodes);
            Parts = _gpu.Upload(parts);
            Residency = _gpu.Upload(mapping);
            Layout = GpuBinding.Layout(_gpu, [
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute,
                    (ulong)Marshal.SizeOf<Configuration>()),
                GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(2, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(3, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(4, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(5, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(6, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(7, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute)
            ]);
            var shader = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device, PbrShaderSource.Compile("stream_traversal.wgsl"), "pbr-stream-traversal"));
            var pipelineLayout = GpuBinding.PipelineLayout(_gpu, Layout);
            ResetFeedback = Compute(shader, pipelineLayout, "reset_feedback");
            ResetDraws = Compute(shader, pipelineLayout, "reset_draws");
            SeedRoots = Compute(shader, pipelineLayout, "seed_roots");
            PrepareEven = Compute(shader, pipelineLayout, "prepare_even");
            PrepareOdd = Compute(shader, pipelineLayout, "prepare_odd");
            TraverseEven = Compute(shader, pipelineLayout, "traverse_even");
            TraverseOdd = Compute(shader, pipelineLayout, "traverse_odd");
            PrepareExpansion = Compute(shader, pipelineLayout, "prepare_expansion");
            ExpansionLayout = GpuBinding.Layout(_gpu, [
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute,
                    (ulong)Marshal.SizeOf<Configuration>()),
                GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(2, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(3, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(4, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(5, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute)
            ]);
            var expansionShader = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device,
                PbrShaderSource.Compile("stream_expand.wgsl"), "pbr-stream-expand"));
            Expand = Compute(expansionShader, GpuBinding.PipelineLayout(_gpu, ExpansionLayout), "expand");
        }
        catch {
            _gpu.Dispose();
            throw;
        }
    }

    internal static ulong MaximumCutTriangles(PbrSceneStream.HierarchyInfo tree)
        => MaximumCut(tree).Triangles;

    internal static (ulong Triangles, ulong Nodes, ulong Records) MaximumCut(PbrSceneStream.HierarchyInfo tree)
    {
        var cuts = new (ulong Triangles, ulong Nodes, ulong Records)[tree.Nodes.Length];
        for (var n = tree.Nodes.Length - 1; n >= 0; n--) {
            var node = tree.Nodes[n];
            ulong triangles = 0, nodes = 0, records = 0, ownRecords = 0;
            foreach (var part in node.Pages)
                ownRecords = checked(ownRecords + (((ulong)part.Count + WorkBlockTriangles - 1) / WorkBlockTriangles));
            for (var c = 0; c < node.ChildCount; c++) {
                var child = cuts[node.Children + c];
                triangles = checked(triangles + child.Triangles);
                nodes = checked(nodes + child.Nodes);
                records = checked(records + child.Records);
            }
            cuts[n] = (System.Math.Max((ulong)node.Triangles, triangles), System.Math.Max(1ul, nodes),
                System.Math.Max(ownRecords, records));
        }
        ulong totalTriangles = 0, totalNodes = 0, totalRecords = 0;
        for (var root = 0; root < tree.Roots; root++) {
            totalTriangles = checked(totalTriangles + cuts[root].Triangles);
            totalNodes = checked(totalNodes + cuts[root].Nodes);
            totalRecords = checked(totalRecords + cuts[root].Records);
        }
        return (totalTriangles, totalNodes, totalRecords);
    }

    private Entity Compute(Entity shader, Entity layout, string entry)
    {
        var name = Encoding.UTF8.GetBytes(entry);
        fixed (byte* pointer = name) {
            var descriptor = WGPUComputePipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)layout.GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Compute.Module = (WGPUShaderModule*)shader.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            descriptor.Compute.EntryPoint = new() { Data = pointer, Length = (nuint)name.Length };
            return _gpu.Own(Wgpu.CreateComputePipeline(_gpu.Device, descriptor));
        }
    }

    public void Publish(int slot, uint4 data)
        => Wgpu.WriteBuffer<uint4>(_gpu.Queue, Residency.GetWgpu<WGPUBuffer>(), checked((ulong)slot * 16), [data]);

    public void Dispose() => _gpu.Dispose();
}
