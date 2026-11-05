namespace Sia.Engine.Rendering.Pbr;

internal readonly record struct PbrGeometryInitializationStatistics(
    double RootShapeMilliseconds,
    double InstancesMilliseconds,
    double TransparentBootstrapMilliseconds,
    double ArenaMilliseconds,
    double TransparentCopyMilliseconds,
    double ResidencyMilliseconds,
    double HierarchyMilliseconds,
    double BindingsMilliseconds,
    double TotalMilliseconds);

internal readonly record struct PbrResidencyInitializationStatistics(
    double LayoutMilliseconds,
    double RootPackingMilliseconds,
    double RootUploadMilliseconds,
    double RootDrawsMilliseconds,
    double CpuSelectionMilliseconds,
    int UploadedRootPages,
    long UploadedRootVertices,
    long UploadedRootTriangles,
    long UploadedRootBytes,
    double TotalMilliseconds)
{
    public double MaximumRootUploadMilliseconds { get; init; }
    public long RootAllocatedBytes { get; init; }
    public int RootGen0Collections { get; init; }
    public int RootGen1Collections { get; init; }
    public int RootGen2Collections { get; init; }
}

// Process-wide GC/allocation deltas may include concurrent workers. No collection
// is forced, and these managed counters do not measure WASM linear-memory growth.
internal readonly record struct PbrBufferUploadInitializationStatistics(
    ulong Bytes,
    double CreateMilliseconds,
    double WriteMilliseconds,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

internal readonly record struct PbrHierarchyInitializationStatistics(
    double DepthMilliseconds,
    double UploadMilliseconds,
    double LayoutMilliseconds,
    double TraversalShaderMilliseconds,
    double TraversalPipelinesMilliseconds,
    double ExpansionShaderMilliseconds,
    double ExpansionPipelineMilliseconds,
    int Nodes,
    int Parts,
    int Pages,
    double TotalMilliseconds)
{
    public PbrBufferUploadInitializationStatistics NodesUpload { get; init; }
    public PbrBufferUploadInitializationStatistics PartsUpload { get; init; }
    public PbrBufferUploadInitializationStatistics MappingUpload { get; init; }
}
