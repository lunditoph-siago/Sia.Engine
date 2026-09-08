using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Benchmarks;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

var suite = "smoke";
var output = "visibility-benchmark.json";
var warmup = 10;
var frames = 30;
var timing = true;
var refinementBudget = int.MaxValue;
var refinementNodes = int.MaxValue;
var inFlightFrames = 1;
string? assetPath = null;
string? cookPath = null;
string? sourcePath = null;
string? scenePath = null;
string? stripScenePath = null;
var textureSize = 256;
var attribution = "";
var compress = false;
var view = "clip";
var fixture = "grid";
var gridSize = 64;
var buildSettings = MeshPatchBuildSettings.Default;
if (args.Contains("--help")) {
    Console.WriteLine("""
        Cook without creating a GPU device:
          --strip-scene-lod OUTPUT.siapbr --source INPUT.siapbr
          --cook-scene OUTPUT.siapbr --source SCENE.gltf|SCENE.glb [--texture-size 256 --attribution TEXT]
          --cook PATH --fixture grid|terrain|plane --size N
          [--leaf-triangles N --children N --ratio F --normal-weight F --uv-weight F]
          --cook PATH --source MESH.ply [--compress]
        PLY input: ASCII triangle positions; remove unused vertices, normalize longest axis to 1.6,
        generate smooth normals, discard scan attributes. --compress stores lossless GZip data.
        Existing files are never overwritten. The output includes build diagnostics and timings.
        Benchmark a cooked asset:
          --asset PATH [--suite smoke|instances --warmup N --frames N --output PATH]
        Benchmark a generated fixture:
          --suite smoke|scale|instances [--fixture grid|terrain|plane --size N]
        Framing: --view clip|frontal (frontal fits the asset bounds with a fixed orthographic view).
        Rendering options: --refinement-budget N --refinement-nodes N --in-flight N --no-timing
        """);
    return;
}
for (var i = 0; i < args.Length; i++) {
    if (args[i] == "--compress") { compress = true; continue; }
    if (args[i] == "--no-timing") { timing = false; continue; }
    if (i + 1 >= args.Length) { throw new ArgumentException($"Missing value for {args[i]}."); }
    var name = args[i++];
    switch (name) {
        case "--suite": suite = args[i]; break;
        case "--output": output = args[i]; break;
        case "--warmup": warmup = int.Parse(args[i]); break;
        case "--frames": frames = int.Parse(args[i]); break;
        case "--refinement-budget": refinementBudget = int.Parse(args[i]); break;
        case "--refinement-nodes": refinementNodes = int.Parse(args[i]); break;
        case "--in-flight": inFlightFrames = int.Parse(args[i]); break;
        case "--asset": assetPath = args[i]; break;
        case "--cook": cookPath = args[i]; break;
        case "--source": sourcePath = args[i]; break;
        case "--cook-scene": scenePath = args[i]; break;
        case "--strip-scene-lod": stripScenePath = args[i]; break;
        case "--texture-size": textureSize = int.Parse(args[i]); break;
        case "--attribution": attribution = args[i]; break;
        case "--view": view = args[i]; break;
        case "--fixture": fixture = args[i]; break;
        case "--size": gridSize = int.Parse(args[i]); break;
        case "--leaf-triangles": buildSettings = buildSettings with { MaxLeafTriangles = int.Parse(args[i]) }; break;
        case "--children": buildSettings = buildSettings with { MaxChildren = int.Parse(args[i]) }; break;
        case "--ratio": buildSettings = buildSettings with { ParentTriangleRatio = float.Parse(args[i], CultureInfo.InvariantCulture) }; break;
        case "--normal-weight": buildSettings = buildSettings with { NormalWeight = float.Parse(args[i], CultureInfo.InvariantCulture) }; break;
        case "--uv-weight": buildSettings = buildSettings with { UVWeight = float.Parse(args[i], CultureInfo.InvariantCulture) }; break;
        default: throw new ArgumentException("Unknown option: " + name);
    }
}
if (stripScenePath is not null) {
    if (sourcePath is null || args.Any(option => option.StartsWith("--", StringComparison.Ordinal)
        && option is not ("--strip-scene-lod" or "--source"))) {
        throw new ArgumentException("--strip-scene-lod requires only --source INPUT.siapbr.");
    }
    var source = PbrSceneAsset.Decode(File.ReadAllBytes(sourcePath), 512 * 1024 * 1024);
    var geometry = source.Geometry.ToArray().Select(asset => asset.ExtractFinest()).ToArray();
    var scene = PbrSceneAsset.Create(geometry, source.Materials.Span, source.Instances.Span, source.Attribution);
    var bytes = scene.Encode();
    _ = PbrSceneAsset.Decode(bytes, 512 * 1024 * 1024);
    WriteAsset(stripScenePath, bytes);
    Console.WriteLine(JsonSerializer.Serialize(new { Path = Path.GetFullPath(stripScenePath), Bytes = bytes.Length }));
    return;
}
if (scenePath is not null) {
    if (sourcePath is null || args.Any(option => option.StartsWith("--", StringComparison.Ordinal) && option is not
        ("--cook-scene" or "--source" or "--texture-size" or "--attribution" or "--leaf-triangles" or "--children" or "--ratio" or "--normal-weight" or "--uv-weight"))) {
        throw new ArgumentException("--cook-scene requires --source and accepts only texture, attribution and mesh build settings.");
    }
    var watch = Stopwatch.StartNew();
    var scene = GltfScene.Read(sourcePath, textureSize, attribution, buildSettings);
    var bytes = scene.Encode();
    var decoded = PbrSceneAsset.Decode(bytes, 512 * 1024 * 1024);
    WriteAsset(scenePath, bytes);
    Console.WriteLine(JsonSerializer.Serialize(new {
        Path = Path.GetFullPath(scenePath), Bytes = bytes.Length, Geometry = decoded.Geometry.Length,
        Materials = decoded.Materials.Length, Instances = decoded.Instances.Length, Milliseconds = watch.Elapsed.TotalMilliseconds
    }));
    return;
}
if (suite is not ("smoke" or "scale" or "instances") || warmup < 0 || frames < 1 || refinementBudget < 0 || refinementNodes < 0 || inFlightFrames is < 1 or > 64) {
    throw new ArgumentException("Invalid suite, frame counts, or refinement budget.");
}
if (gridSize < 1 || fixture is not ("grid" or "terrain" or "plane") || (assetPath is not null && (cookPath is not null || suite == "scale"))) {
    throw new ArgumentException("Invalid asset/fixture options. --asset cannot be combined with --cook or --suite scale.");
}
if (assetPath is not null && args.Any(option => option is "--fixture" or "--size" or "--leaf-triangles" or "--children"
    or "--ratio" or "--normal-weight" or "--uv-weight")) {
    throw new ArgumentException("Cooked assets already contain their geometry and build settings; fixture/build options require generated input.");
}
if (view is not ("clip" or "frontal") || (compress && cookPath is null)
    || (sourcePath is not null && (cookPath is null || args.Any(option => option is "--fixture" or "--size")))) {
    throw new ArgumentException("--source requires --cook without fixture/size; --compress requires --cook; --view must be clip or frontal.");
}
#if DEBUG
throw new InvalidOperationException("Run this benchmark with --configuration Release.");
#endif
if (cookPath is not null) {
    MeshData source;
    using (var reader = sourcePath is null ? null : File.OpenText(sourcePath)) {
        source = reader is null ? Assets.Create(fixture, gridSize) : PlyMesh.Read(reader);
    }
    var watch = Stopwatch.StartNew();
    var cooked = MeshPatchAsset.Cook(source, buildSettings);
    var cookMilliseconds = watch.Elapsed.TotalMilliseconds;
    watch.Restart();
    var bytes = compress ? cooked.EncodeCompressed() : cooked.Encode();
    var encodeMilliseconds = watch.Elapsed.TotalMilliseconds;
    watch.Restart();
    MeshPatchAsset.Decode(bytes);
    var validateMilliseconds = watch.Elapsed.TotalMilliseconds;
    var destination = WriteAsset(cookPath, bytes);
    Console.WriteLine(JsonSerializer.Serialize(new {
        Asset = destination, Source = sourcePath, Fixture = sourcePath is null ? fixture : null,
        GridSize = sourcePath is null ? (int?)gridSize : null, Vertices = source.Vertices.Length, Bytes = bytes.Length, Compressed = compress,
        MeshPatchAsset.FormatVersion, cooked.BuilderVersion, cooked.SourceHash, cooked.Settings,
        cooked.Build.SourceTriangleCount, cooked.Build.RemovedDegenerateTriangleCount,
        cooked.Build.SimplificationCount, cooked.Build.TargetMissCount, cooked.Build.UnreducedGroupCount,
        Nodes = cooked.Build.Tree.Nodes.Length, cooked.Build.Tree.RootCount,
        CookMilliseconds = cookMilliseconds, EncodeMilliseconds = encodeMilliseconds, ValidateMilliseconds = validateMilliseconds
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
MeshPatchAsset? loaded = null;
double readMilliseconds = 0, decodeMilliseconds = 0;
if (assetPath is not null) {
    var watch = Stopwatch.StartNew();
    var bytes = await File.ReadAllBytesAsync(assetPath);
    readMilliseconds = watch.Elapsed.TotalMilliseconds;
    watch.Restart();
    loaded = MeshPatchAsset.Decode(bytes);
    decodeMilliseconds = watch.Elapsed.TotalMilliseconds;
}
using var gpu = await GpuDevice.CreateAsync(timing);
Console.WriteLine($"{gpu.Description.Device}; {gpu.Description.Backend}; GPU timing: {gpu.TimingEnabled}");
var results = new List<CaseResult>();
int[] sizes = loaded is not null ? [0] : suite == "scale" ? [724, 2237, 5000] : [gridSize];
string[] scenarios = suite switch {
    "scale" => ["near", "far"], "instances" => ["instanced"],
    _ => ["near", "far", "occluded", "offscreen", "nonuniform"]
};
foreach (var size in sizes) {
    var sourceTriangles = loaded?.Build.SourceTriangleCount ?? checked(size * size * 2);
    MeshPatchBuildResult? build = loaded?.Build;
    double buildSeconds = 0;
    foreach (var resolution in new (uint Width, uint Height)[] { (640, 360), (1280, 720) }) {
        foreach (var scenario in scenarios) {
            var instances = Assets.Instances(scenario);
            var minimumResidentTriangleBytes = checked((ulong)(loaded?.Build.Tree.FinestTriangleCount ?? sourceTriangles) * 16);
            var input = new CaseInput(loaded is null ? size : null, sourceTriangles, instances.Length, scenario, resolution.Width, resolution.Height,
                18000, refinementBudget, refinementNodes);
            if (minimumResidentTriangleBytes > gpu.Limits.MaxStorageBufferBindingSize || minimumResidentTriangleBytes > gpu.Limits.MaxBufferSize) {
                results.Add(new(input, "capacity-rejected", $"Resident triangle records alone require at least {minimumResidentTriangleBytes} bytes before parent representations; device limits are {gpu.Limits.MaxStorageBufferBindingSize} storage binding / {gpu.Limits.MaxBufferSize} buffer bytes.", null, null, null));
                Console.WriteLine($"{sourceTriangles} triangles / {scenario}: capacity-rejected ({minimumResidentTriangleBytes} minimum resident triangle bytes)");
                continue;
            }
            if (build is null) {
                var watch = Stopwatch.StartNew();
                build = MeshPatchBuilder.Build(Assets.Create(fixture, size), buildSettings);
                buildSeconds = watch.Elapsed.TotalSeconds;
                Console.WriteLine($"Cooked {sourceTriangles} triangles in {buildSeconds:F3} s; {build.Value.Tree.Nodes.Length} nodes.");
            }
            var tree = build.Value.Tree;
            var bounds = tree.RootCount == 0 ? default : tree.Nodes.Span[0].Bounds;
            foreach (var node in tree.Nodes.Span[..tree.RootCount]) {
                bounds = new(math.min(bounds.Min, node.Bounds.Min), math.max(bounds.Max, node.Bounds.Max));
            }
            var asset = new AssetResult(loaded is null ? buildSeconds : null, tree.Nodes.Length, tree.RootCount,
                tree.Nodes.Span[..tree.RootCount].ToArray().Sum(n => n.TriangleCount), tree.Nodes.ToArray().Sum(n => (long)n.TriangleCount),
                assetPath, loaded?.SourceHash, loaded?.Settings ?? buildSettings, loaded?.BuilderVersion, readMilliseconds, decodeMilliseconds);
            try {
                var startup = Stopwatch.StartNew();
                using var scene = new BenchmarkScene(gpu, tree, instances, resolution.Width, resolution.Height,
                    new(int.MaxValue, int.MaxValue, input.TriangleBudget) {
                        MaxRefinementCandidates = input.RefinementBudget, MaxRefinementNodes = input.RefinementNodes
                    }, inFlightFrames);
                var projection = Assets.Projection(scenario, view == "frontal" ? bounds : null, (float)resolution.Width / resolution.Height);
                var setupMilliseconds = startup.Elapsed.TotalMilliseconds;
                startup.Restart();
                await foreach (var _ in scene.RenderAsync([projection])) { }
                var firstFrameMilliseconds = startup.Elapsed.TotalMilliseconds;
                await foreach (var _ in scene.RenderAsync(Enumerable.Repeat(projection, warmup))) { }
                var samples = new FrameSample[frames];
                var elapsed = Stopwatch.StartNew();
                var sampleIndex = 0;
                await foreach (var sample in scene.RenderAsync(Enumerable.Repeat(projection, frames))) { samples[sampleIndex++] = sample; }
                elapsed.Stop();
                var capacity = new CapacityResult(scene.WorkCapacityBytes, scene.BufferCapacityBytes, scene.GraphPassCount);
                results.Add(new(input, "measured", null, asset, capacity, samples,
                    new(elapsed.Elapsed.TotalMilliseconds, frames / elapsed.Elapsed.TotalSeconds, scene.PeakInFlight, scene.ReadbackCapacityBytes),
                    new(setupMilliseconds, firstFrameMilliseconds)));
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
    SchemaVersion = 4, View = view, CreatedUtc = DateTimeOffset.UtcNow, Suite = suite, Fixture = loaded is null ? fixture : null,
    WarmupFrames = warmup, MeasuredFrames = frames, InFlightFrames = inFlightFrames,
    BuildConfiguration = "Release", Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    Adapter = gpu.Description, TimingRequested = timing, gpu.TimingEnabled,
    Limits = new { gpu.Limits.MaxBufferSize, gpu.Limits.MaxStorageBufferBindingSize, gpu.Limits.MaxComputeWorkgroupsPerDimension },
    TimingStages = VisibilityPbrFeature.GpuTimingStages.ToArray(),
    Method = "Bounded frame stream; depth one serializes submissions. A separate first frame is drained before warmup; both are excluded from measured samples. SceneSetupAndUploadMilliseconds includes CPU resource setup and upload enqueue, not GPU upload completion. FirstFrameAndReadbackMilliseconds includes graph creation and completion. BuildSeconds is unavailable when loading cooked input; ReadMilliseconds and DecodeAndValidateMilliseconds are CPU startup costs. Warmup is drained and excluded. Pipeline elapsed time includes submission through final result consumption; FramesPerSecond is measured stream throughput. EndToEndMilliseconds runs from frame preparation to result readback. GPU stage sums are neither throughput nor total frame latency. WaitAndReadMilliseconds measures waiting when consuming the oldest pending result. ViewportPixelsPerEmittedTriangle is a viewport/work ratio, not measured coverage. BufferCapacityBytes includes graph buffers and every readback slot, excluding textures, driver allocations and query-set storage. Each measured frame has one graph submission, two geometry indirect draws and one output draw.",
    Summaries = results.Select(result => new {
        result.Input, result.Status,
        GpuStageSum = result.Samples is { } samples ? Distribution.From(samples.Select(s => s.GpuMilliseconds?.Values.Sum() ?? double.NaN)) : null,
        GpuStages = result.Samples is { Length: > 0 } timed && timed[0].GpuMilliseconds is not null
            ? VisibilityPbrFeature.GpuTimingStages.ToArray().ToDictionary(name => name, name => Distribution.From(timed.Select(s => s.GpuMilliseconds![name]))) : null,
        CpuPrepare = result.Samples is { } prepared ? Distribution.From(prepared.Select(s => s.PrepareMilliseconds)) : null,
        CpuEncodeAndSubmit = result.Samples is { } encoded ? Distribution.From(encoded.Select(s => s.EncodeAndSubmitMilliseconds)) : null,
        EndToEnd = result.Samples is { } completed ? Distribution.From(completed.Select(s => s.EndToEndMilliseconds)) : null,
        result.Pipeline
    }).ToArray(),
    Results = results
};
var path = Path.GetFullPath(output);
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(path);

static string WriteAsset(string path, byte[] bytes)
{
    var destination = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
    var ownsTemporary = false;
    try {
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            ownsTemporary = true;
            file.Write(bytes);
        }
        File.Move(temporary, destination, overwrite: false);
    }
    finally { if (ownsTemporary) { File.Delete(temporary); } }
    return destination;
}

internal sealed record CaseInput(int? GridSize, int SourceTriangles, int InstanceCount, string Scenario, uint Width, uint Height,
    int TriangleBudget, int RefinementBudget, int RefinementNodes);
internal sealed record AssetResult(double? BuildSeconds, int Nodes, int Roots, int RootTriangles, long ResidentTriangles,
    string? Path, string? SourceHash, MeshPatchBuildSettings Settings, int? BuilderVersion, double ReadMilliseconds, double DecodeAndValidateMilliseconds);
internal sealed record CapacityResult(ulong WorkCapacityBytes, ulong BufferCapacityBytes, int GraphPassCount);
internal sealed record CaseResult(CaseInput Input, string Status, string? Reason, AssetResult? Asset, CapacityResult? Capacity, FrameSample[]? Samples,
    PipelineResult? Pipeline = null, StartupResult? Startup = null);
internal sealed record StartupResult(double SceneSetupAndUploadMilliseconds, double FirstFrameAndReadbackMilliseconds);
internal sealed record PipelineResult(double ElapsedMilliseconds, double FramesPerSecond, int PeakInFlight, ulong ReadbackCapacityBytes);
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
