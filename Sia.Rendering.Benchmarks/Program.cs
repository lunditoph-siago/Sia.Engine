using System.Diagnostics;
using System.Text.Json;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Benchmarks;
using Sia.Engine.Rendering.Pbr;

var suite = "smoke";
var output = "visibility-benchmark.json";
var warmup = 10;
var frames = 30;
var timing = true;
var refinementBudget = int.MaxValue;
var refinementNodes = int.MaxValue;
for (var i = 0; i < args.Length; i++) {
    if (args[i] == "--no-timing") { timing = false; continue; }
    if (i + 1 >= args.Length) { throw new ArgumentException("Expected --suite smoke|scale|instances, --output PATH, --warmup N, --frames N, --refinement-budget N, --refinement-nodes N, or --no-timing."); }
    var name = args[i++];
    switch (name) {
        case "--suite": suite = args[i]; break;
        case "--output": output = args[i]; break;
        case "--warmup": warmup = int.Parse(args[i]); break;
        case "--frames": frames = int.Parse(args[i]); break;
        case "--refinement-budget": refinementBudget = int.Parse(args[i]); break;
        case "--refinement-nodes": refinementNodes = int.Parse(args[i]); break;
        default: throw new ArgumentException("Unknown option: " + name);
    }
}
if (suite is not ("smoke" or "scale" or "instances") || warmup < 0 || frames < 1 || refinementBudget < 0 || refinementNodes < 0) {
    throw new ArgumentException("Invalid suite, frame counts, or refinement budget.");
}
#if DEBUG
throw new InvalidOperationException("Run this benchmark with --configuration Release.");
#endif
using var gpu = new GpuDevice(timing);
Console.WriteLine($"{gpu.Description.Device}; {gpu.Description.Backend}; GPU timing: {gpu.TimingEnabled}");
var results = new List<CaseResult>();
int[] sizes = suite == "scale" ? [724, 2237, 5000] : [64];
string[] scenarios = suite switch {
    "scale" => ["near", "far"], "instances" => ["instanced"],
    _ => ["near", "far", "occluded", "offscreen", "nonuniform"]
};
foreach (var size in sizes) {
    var sourceTriangles = checked(size * size * 2);
    MeshPatchBuildResult? build = null;
    double buildSeconds = 0;
    foreach (var resolution in new (uint Width, uint Height)[] { (640, 360), (1280, 720) }) {
        foreach (var scenario in scenarios) {
            var instances = Assets.Instances(scenario);
            var minimumResidentTriangleBytes = checked((ulong)sourceTriangles * 16);
            var input = new CaseInput(size, sourceTriangles, instances.Length, scenario, resolution.Width, resolution.Height,
                18000, refinementBudget, refinementNodes);
            if (minimumResidentTriangleBytes > gpu.Limits.MaxStorageBufferBindingSize || minimumResidentTriangleBytes > gpu.Limits.MaxBufferSize) {
                results.Add(new(input, "capacity-rejected", $"Resident triangle records alone require at least {minimumResidentTriangleBytes} bytes before parent representations; device limits are {gpu.Limits.MaxStorageBufferBindingSize} storage binding / {gpu.Limits.MaxBufferSize} buffer bytes.", null, null, null));
                Console.WriteLine($"{sourceTriangles} triangles / {scenario}: capacity-rejected ({minimumResidentTriangleBytes} minimum resident triangle bytes)");
                continue;
            }
            if (build is null) {
                var watch = Stopwatch.StartNew();
                build = MeshPatchBuilder.Build(Assets.Grid(size));
                buildSeconds = watch.Elapsed.TotalSeconds;
                Console.WriteLine($"Cooked {sourceTriangles} triangles in {buildSeconds:F3} s; {build.Value.Tree.Nodes.Length} nodes.");
            }
            var tree = build.Value.Tree;
            var asset = new AssetResult(buildSeconds, tree.Nodes.Length, tree.RootCount,
                tree.Nodes.Span[..tree.RootCount].ToArray().Sum(n => n.TriangleCount), tree.Nodes.ToArray().Sum(n => (long)n.TriangleCount));
            try {
                using var scene = new BenchmarkScene(gpu, tree, instances, resolution.Width, resolution.Height,
                    new(int.MaxValue, int.MaxValue, input.TriangleBudget) {
                        MaxRefinementCandidates = input.RefinementBudget, MaxRefinementNodes = input.RefinementNodes
                    });
                var projection = Assets.Projection(scenario);
                for (var frame = 0; frame < warmup; frame++) { scene.Render(projection); }
                var samples = new FrameSample[frames];
                for (var frame = 0; frame < frames; frame++) { samples[frame] = scene.Render(projection); }
                var capacity = new CapacityResult(scene.WorkCapacityBytes, scene.BufferCapacityBytes, scene.GraphPassCount);
                results.Add(new(input, "measured", null, asset, capacity, samples));
                var median = Distribution.From(samples.Select(s => s.GpuMilliseconds?.Values.Sum() ?? double.NaN));
                var gpuTime = median is null ? "unavailable" : $"{median.MedianMilliseconds:F3} ms";
                Console.WriteLine($"{sourceTriangles} / {resolution.Width}x{resolution.Height} / {scenario}: {samples[^1].Counters.MainTriangles + samples[^1].Counters.PostTriangles} emitted; GPU stage sum median {gpuTime}.");
            }
            catch (ArgumentException error) {
                results.Add(new(input, "capacity-or-input-rejected", error.Message, asset, null, null));
            }
        }
    }
}
var report = new {
    SchemaVersion = 1, CreatedUtc = DateTimeOffset.UtcNow, Suite = suite, WarmupFrames = warmup, MeasuredFrames = frames,
    BuildConfiguration = "Release", Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    Adapter = gpu.Description, TimingRequested = timing, gpu.TimingEnabled,
    Limits = new { gpu.Limits.MaxBufferSize, gpu.Limits.MaxStorageBufferBindingSize, gpu.Limits.MaxComputeWorkgroupsPerDimension },
    TimingStages = VisibilityPbrFeature.GpuTimingStages.ToArray(),
    Method = "Serialized frames; warmup excluded. GPU timestamps measure individual stages; their sum is not throughput or total frame latency. CPU encode excludes blocking readback; the wait/read interval is reported separately. ViewportPixelsPerEmittedTriangle is a viewport/work ratio, not measured triangle coverage. BufferCapacityBytes sums graph buffer descriptors, including benchmark readback; it excludes textures, driver allocations and query-set storage. Each measured frame has one graph command-buffer submission, two geometry indirect draws and one output draw.",
    Summaries = results.Select(result => new {
        result.Input, result.Status,
        GpuStageSum = result.Samples is { } samples ? Distribution.From(samples.Select(s => s.GpuMilliseconds?.Values.Sum() ?? double.NaN)) : null,
        GpuStages = result.Samples is { Length: > 0 } timed && timed[0].GpuMilliseconds is not null
            ? VisibilityPbrFeature.GpuTimingStages.ToArray().ToDictionary(name => name, name => Distribution.From(timed.Select(s => s.GpuMilliseconds![name]))) : null,
        CpuPrepare = result.Samples is { } prepared ? Distribution.From(prepared.Select(s => s.PrepareMilliseconds)) : null,
        CpuEncodeAndSubmit = result.Samples is { } encoded ? Distribution.From(encoded.Select(s => s.EncodeAndSubmitMilliseconds)) : null
    }).ToArray(),
    Results = results
};
var path = Path.GetFullPath(output);
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(path);

internal sealed record CaseInput(int GridSize, int SourceTriangles, int InstanceCount, string Scenario, uint Width, uint Height,
    int TriangleBudget, int RefinementBudget, int RefinementNodes);
internal sealed record AssetResult(double BuildSeconds, int Nodes, int Roots, int RootTriangles, long ResidentTriangles);
internal sealed record CapacityResult(ulong WorkCapacityBytes, ulong BufferCapacityBytes, int GraphPassCount);
internal sealed record CaseResult(CaseInput Input, string Status, string? Reason, AssetResult? Asset, CapacityResult? Capacity, FrameSample[]? Samples);
internal sealed record Distribution(double MedianMilliseconds, double P95Milliseconds, double MaximumMilliseconds)
{
    public static Distribution? From(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) { return null; }
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
        return new(median, sorted[System.Math.Max(0, (int)System.Math.Ceiling(sorted.Length * 0.95) - 1)], sorted[^1]);
    }
}
