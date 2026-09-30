using System.Runtime.InteropServices;
using System.Text;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed unsafe class PbrGpuHierarchy : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Node(float4 Min, float4 Max, uint4 Links, uint4 Owner);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Configuration(
        float4x4 Projection,
        uint4 Table,
        float4 Screen,
        uint4 Output);

    private readonly GpuResources _gpu;

    public Entity Nodes { get; }
    public Entity Parts { get; }
    public Entity Residency { get; }
    public Entity Layout { get; }
    public Entity ResetFeedback { get; }
    public Entity ResetDraws { get; }
    public Entity Traverse { get; }

    public uint RootBase { get; }
    public uint RootCount { get; }
    public uint PageCount { get; }
    public uint SingleCapacity { get; }
    public uint DoubleCapacity { get; }

    public ulong Bytes => _gpu.Bytes;

    public PbrGpuHierarchy(in GpuFrame frame, ulong budget, ReadOnlySpan<Node> nodes, ReadOnlySpan<uint4> parts,
        ReadOnlySpan<uint4> mapping, uint rootBase, uint rootCount, uint singleCapacity, uint doubleCapacity)
    {
        _gpu = new(frame, budget);
        (RootBase, RootCount, PageCount, SingleCapacity, DoubleCapacity) =
            (rootBase, rootCount, (uint)mapping.Length, singleCapacity, doubleCapacity);
        try {
            Nodes = _gpu.Upload(nodes);
            Parts = _gpu.Upload(parts);
            Residency = _gpu.Upload(mapping);
            Layout = GpuBinding.Layout(_gpu, [
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute, 112),
                GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(2, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(3, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(4, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(5, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(6, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)
            ]);
            var shader = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device, PbrShaderSource.Compile("stream_traversal.wgsl"), "pbr-stream-traversal"));
            var pipelineLayout = GpuBinding.PipelineLayout(_gpu, Layout);
            ResetFeedback = Compute(shader, pipelineLayout, "reset_feedback");
            ResetDraws = Compute(shader, pipelineLayout, "reset_draws");
            Traverse = Compute(shader, pipelineLayout, "traverse");
        }
        catch {
            _gpu.Dispose();
            throw;
        }
    }

    internal static ulong MaximumCutTriangles(PbrSceneStream.HierarchyInfo tree)
    {
        var cuts = new ulong[tree.Nodes.Length];
        for (var n = tree.Nodes.Length - 1; n >= 0; n--) {
            var node = tree.Nodes[n];
            ulong children = 0;
            for (var c = 0; c < node.ChildCount; c++) children = checked(children + cuts[node.Children + c]);
            cuts[n] = System.Math.Max((ulong)node.Triangles, children);
        }
        ulong total = 0;
        for (var root = 0; root < tree.Roots; root++) total = checked(total + cuts[root]);
        return total;
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
