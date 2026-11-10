using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
        float4 ForwardPixels,
        uint4 Shadow = default);

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
    public Entity PrepareStableWork { get; }
    public Entity PrefixStableWork { get; }
    public Entity ClearStableScan { get; }
    public Entity CountStableScan { get; }
    public Entity ScanStableBlocks { get; }
    public Entity PrefixStableBlocks { get; }
    public Entity AssignStableScan { get; }
    public Entity Expand { get; }
    public Entity CompareShadowCut { get; }
    public Entity ShadowClear { get; }

    public uint RootBase { get; }
    public uint RootCount { get; }
    public uint PageCount { get; }
    public uint SourceNodeCount { get; }
    public uint SingleCapacity { get; }
    public uint DoubleCapacity { get; }
    public uint NodeCapacity { get; }
    public ulong ShadowNodeCount { get; }
    public int Levels { get; }

    public ulong Bytes => _gpu.Bytes;
    internal long FeedbackFrame { get; set; }
    internal long RecoveryEpoch { get; set; }
    internal PbrHierarchyInitializationStatistics InitializationStatistics { get; }

    public PbrGpuHierarchy(in GpuFrame frame, ulong budget, ReadOnlySpan<Node> nodes, ReadOnlySpan<uint4> parts,
        ReadOnlySpan<uint4> mapping, uint rootBase, uint rootCount, uint singleCapacity, uint doubleCapacity,
        uint nodeCapacity, Entity instances, ulong shadowNodeCount)
    {
        var started = Stopwatch.GetTimestamp();
        _gpu = new(frame, budget);
        Instances = instances;
        (RootBase, RootCount, PageCount, SingleCapacity, DoubleCapacity) =
            (rootBase, rootCount, (uint)mapping.Length, singleCapacity, doubleCapacity);
        SourceNodeCount = checked((uint)nodes.Length);
        NodeCapacity = nodeCapacity;
        ShadowNodeCount = shadowNodeCount;
        var depths = new int[nodes.Length];
        for (var i = 0; i < nodes.Length; i++) {
            var parent = nodes[i].Owner.x;
            if (parent != uint.MaxValue && parent >= i)
                throw new ArgumentException("GPU hierarchy parents must precede their children.", nameof(nodes));
            depths[i] = parent == uint.MaxValue ? 1 : checked(depths[(int)parent] + 1);
            Levels = System.Math.Max(Levels, depths[i]);
        }
        var depthComplete = Stopwatch.GetTimestamp();
        try {
            Nodes = UploadObserved(nodes, out var nodesUpload);
            Parts = UploadObserved(parts, out var partsUpload);
            Residency = UploadObserved(mapping, out var mappingUpload);
            var uploadComplete = Stopwatch.GetTimestamp();
            Layout = GpuBinding.Layout(_gpu, [
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute,
                    (ulong)Marshal.SizeOf<Configuration>()),
                GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(2, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(3, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(4, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(5, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(6, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(7, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(8, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute)
            ]);
            var layoutComplete = Stopwatch.GetTimestamp();
            var shader = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device, PbrShaderSource.Compile(frame.ResourceWorld, "stream_traversal.wgsl"), "pbr-stream-traversal"));
            var shaderComplete = Stopwatch.GetTimestamp();
            var pipelineLayout = GpuBinding.PipelineLayout(_gpu, Layout);
            ResetFeedback = Compute(shader, pipelineLayout, "reset_feedback");
            ResetDraws = Compute(shader, pipelineLayout, "reset_draws");
            SeedRoots = Compute(shader, pipelineLayout, "seed_roots");
            PrepareEven = Compute(shader, pipelineLayout, "prepare_even");
            PrepareOdd = Compute(shader, pipelineLayout, "prepare_odd");
            TraverseEven = Compute(shader, pipelineLayout, "traverse_even");
            TraverseOdd = Compute(shader, pipelineLayout, "traverse_odd");
            PrepareExpansion = Compute(shader, pipelineLayout, "prepare_expansion");
            PrepareStableWork = Compute(shader, pipelineLayout, "prepare_stable_work");
            PrefixStableWork = Compute(shader, pipelineLayout, "prefix_stable_work");
            ClearStableScan = Compute(shader, pipelineLayout, "clear_stable_scan");
            CountStableScan = Compute(shader, pipelineLayout, "count_stable_scan");
            ScanStableBlocks = Compute(shader, pipelineLayout, "scan_stable_blocks");
            PrefixStableBlocks = Compute(shader, pipelineLayout, "prefix_stable_blocks");
            AssignStableScan = Compute(shader, pipelineLayout, "assign_stable_scan");
            CompareShadowCut = Compute(shader, pipelineLayout, "compare_shadow_cut");
            ShadowClear = CreateShadowClear(shader);
            var traversalComplete = Stopwatch.GetTimestamp();
            ExpansionLayout = GpuBinding.Layout(_gpu, [
                GpuBinding.Buffer(0, WGPUBufferBindingType.Uniform, WGPUShaderStage.Compute,
                    (ulong)Marshal.SizeOf<Configuration>()),
                GpuBinding.Buffer(1, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(2, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(3, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(4, WGPUBufferBindingType.Storage, WGPUShaderStage.Compute),
                GpuBinding.Buffer(5, WGPUBufferBindingType.ReadOnlyStorage, WGPUShaderStage.Compute)
            ]);
            var expansionLayoutComplete = Stopwatch.GetTimestamp();
            var expansionShader = _gpu.Own(Wgpu.CreateWgslShaderModule(_gpu.Device,
                PbrShaderSource.Compile(frame.ResourceWorld, "stream_expand.wgsl"), "pbr-stream-expand"));
            var expansionShaderComplete = Stopwatch.GetTimestamp();
            Expand = Compute(expansionShader, GpuBinding.PipelineLayout(_gpu, ExpansionLayout), "expand");
            var complete = Stopwatch.GetTimestamp();
            static double Ms(long begin, long end) => (end - begin) * 1000d / Stopwatch.Frequency;
            InitializationStatistics = new(Ms(started, depthComplete), Ms(depthComplete, uploadComplete),
                Ms(uploadComplete, layoutComplete) + Ms(traversalComplete, expansionLayoutComplete),
                Ms(layoutComplete, shaderComplete), Ms(shaderComplete, traversalComplete),
                Ms(expansionLayoutComplete, expansionShaderComplete), Ms(expansionShaderComplete, complete),
                nodes.Length, parts.Length, mapping.Length, Ms(started, complete)) {
                    NodesUpload = nodesUpload, PartsUpload = partsUpload, MappingUpload = mappingUpload
                };
        }
        catch {
            _gpu.Dispose();
            throw;
        }
    }

    internal static uint AppendShadowDomain(ref ulong nodes, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var first = nodes;
        nodes = checked(nodes + (ulong)count);
        // Oversized domains disable caching; their normal traversal still works.
        return nodes <= uint.MaxValue ? (uint)first : 0;
    }

    internal static uint ShadowCacheWords(ulong nodes, ulong maximumBytes, int shadowLayers = PbrPointShadow.BaseLayers)
    {
        if (shadowLayers is < PbrPointShadow.BaseLayers or > 31)
            throw new ArgumentOutOfRangeException(nameof(shadowLayers));
        if (nodes == 0 || nodes > uint.MaxValue) return 0;
        var words = (nodes + 31) / 32;
        // One persistent cut per layer plus a serially shared scratch cut.
        return words * (ulong)(shadowLayers + 1) * sizeof(uint) <= maximumBytes ? checked((uint)words) : 0;
    }

    internal static ulong SelectionBufferBytes(ulong workRecords, ulong feedbackBytes, ulong configurationBytes,
        int shadowLayers = PbrPointShadow.BaseLayers)
    {
        if (shadowLayers is < PbrPointShadow.BaseLayers or > 31)
            throw new ArgumentOutOfRangeException(nameof(shadowLayers));
        // Match GpuResources.Buffer, including tiny/empty storage buffers.
        static ulong Buffer(ulong size) => checked((System.Math.Max(16ul, size) + 3) & ~3ul);
        return checked(Buffer(checked(workRecords * 16)) + Buffer(80) + Buffer(16) + Buffer(feedbackBytes)
            + ((ulong)(shadowLayers + 1) * Buffer(configurationBytes)) + (3 * Buffer(checked(feedbackBytes + 64))));
    }

    private Entity CreateShadowClear(Entity shader)
    {
        var vertex = "clear_shadow_vertex"u8; var fragment = "clear_shadow_depth"u8;
        fixed (byte* v = vertex)
        fixed (byte* f = fragment) {
            var descriptor = WGPURenderPipelineDescriptor.Default;
            descriptor.Layout = (WGPUPipelineLayout*)GpuBinding.PipelineLayout(_gpu).GetWgpu<WGPUPipelineLayout>().DangerousGetHandle();
            descriptor.Vertex.Module = (WGPUShaderModule*)shader.GetWgpu<WGPUShaderModule>().DangerousGetHandle();
            descriptor.Vertex.EntryPoint = new() { Data = v, Length = (nuint)vertex.Length };
            descriptor.Primitive.Topology = WGPUPrimitiveTopology.TriangleList;
            descriptor.Primitive.CullMode = WGPUCullMode.None;
            var depth = WGPUDepthStencilState.Default;
            depth.Format = WGPUTextureFormat.Depth32Float;
            depth.DepthWriteEnabled = WGPUOptionalBool.True;
            depth.DepthCompare = WGPUCompareFunction.Always;
            descriptor.DepthStencil = &depth;
            var output = WGPUFragmentState.Default;
            output.Module = descriptor.Vertex.Module;
            output.EntryPoint = new() { Data = f, Length = (nuint)fragment.Length };
            descriptor.Fragment = &output;
            return _gpu.Own(Wgpu.CreateRenderPipeline(_gpu.Device, descriptor));
        }
    }

    private Entity UploadObserved<T>(ReadOnlySpan<T> data, out PbrBufferUploadInitializationStatistics statistics) where T : unmanaged
    {
        var allocated = GC.GetTotalAllocatedBytes(false);
        var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
        var bytes = checked((ulong)data.Length * (ulong)Unsafe.SizeOf<T>());
        var started = Stopwatch.GetTimestamp();
        var buffer = _gpu.Buffer(bytes, WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst);
        var created = Stopwatch.GetTimestamp();
        if (!data.IsEmpty) Wgpu.WriteBuffer(_gpu.Queue, buffer.GetWgpu<WGPUBuffer>(), 0, data);
        var complete = Stopwatch.GetTimestamp();
        statistics = new(bytes, (created - started) * 1000d / Stopwatch.Frequency,
            (complete - created) * 1000d / Stopwatch.Frequency, GC.GetTotalAllocatedBytes(false) - allocated,
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
        return buffer;
    }

    internal static ulong MaximumCutTriangles(PbrSceneStream.HierarchyInfo tree)
        => MaximumCut(tree).Triangles;

    internal static (ulong Triangles, ulong Nodes, ulong Records) MaximumCut(PbrSceneStream.HierarchyInfo tree)
    {
        var cuts = new (ulong Triangles, ulong Nodes, ulong Records)[tree.Nodes.Length];
        for (var n = tree.Nodes.Length - 1; n >= 0; n--) {
            var node = tree.Nodes[n];
            ulong triangles = 0, nodes = 0, records = 0, ownRecords = 0;
            foreach (var part in tree.GetPages(node))
                ownRecords = checked(ownRecords + (((ulong)part.Count + WorkBlockTriangles - 1) / WorkBlockTriangles));
            for (var c = 0; c < node.ChildCount; c++) {
                var child = cuts[node.FirstChild + c];
                triangles = checked(triangles + child.Triangles);
                nodes = checked(nodes + child.Nodes);
                records = checked(records + child.Records);
            }
            cuts[n] = (System.Math.Max((ulong)node.TriangleCount, triangles), System.Math.Max(1ul, nodes),
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
