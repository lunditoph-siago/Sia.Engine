using System.Diagnostics;
using System.Globalization;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

public static partial class Program
{
    internal static readonly Stopwatch StartupClock = Stopwatch.StartNew();
    internal static int BenchmarkFrames { get; private set; }
    internal static bool BenchmarkMotion { get; private set; }
    internal static RenderQuality Quality { get; private set; } = RenderQuality.Low;
    internal static float RenderScale { get; private set; } = 1;
    internal static int Width { get; private set; } = 1280;
    internal static int Height { get; private set; } = 720;
    internal static bool ImmediatePresent { get; private set; }
    internal static bool GpuTiming { get; private set; }
    internal static int TargetFps { get; private set; }
    public static async Task<int> Main(string[] args)
    {
        try {
#if !BROWSER
            if (args.Length == 4 && args[0] == "--cook-stream" && args[2] == "--output") {
                await CookStreamAsync(args[1], args[3]); return 0;
            }
#endif
#if BROWSER
            BrowserOwner = await BrowserThread.StartAsync();
#endif
            var (pipeline, debugMode, distance, scenePath, finest, camera) = ParseOptions(args);
            using var streamHttp = new HttpClient();
            await using var streaming = scenePath is not null && scenePath.Split('?')[0].EndsWith(".siastream", StringComparison.OrdinalIgnoreCase)
                ? await OpenStreamAsync(scenePath, streamHttp) : null;
            var asset = pipeline == ScenePipeline.Bunny ? await LoadBunnyAsync() : null;
            var scene = streaming?.Bootstrap ?? (pipeline == ScenePipeline.Pbr ? await LoadPbrAsync(scenePath, finest) : null);
            var app = new SceneExampleApp(pipeline, asset, debugMode, distance, scene, finest, camera, streaming);
            scene = null;
#if BROWSER
            try {
                SetLoadingState("Preparing graphics", double.NaN);
                await Task.Delay(20);
                await app.RunAsync();
            }
            finally { await app.StopSceneStreamingAsync(); await BrowserOwner.RunGraphicsAsync(() => { app.Dispose(); return Task.FromResult(0); }); }
#else
            using (app) { try { app.Run(); } finally { await app.StopSceneStreamingAsync(); } }
#endif
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception);
#if BROWSER
            ShowError(exception.Message);
#endif
            return 1;
        }
    }

    private static async Task<MeshPatchAsset> LoadBunnyAsync()
    {
        var timer = Stopwatch.StartNew();
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("Sia.Engine.Example.bunny.siapatch")
            ?? throw new FileNotFoundException("The bundled Bunny asset is missing.");
        Console.WriteLine("Stanford Bunny: Stanford University Computer Graphics Laboratory; https://graphics.stanford.edu/data/3Dscanrep/");
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var bytes = memory.ToArray();
        var readMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
#if BROWSER
        var asset = await BrowserOwner.RunCpuAsync(token => MeshPatchAsset.Decode(bytes, cancellationToken: token));
#else
        var asset = MeshPatchAsset.Decode(bytes);
#endif
        Console.WriteLine($"Patch asset: {bytes.Length} bytes; read {readMilliseconds:F2} ms, decode/validate {timer.Elapsed.TotalMilliseconds:F2} ms; {asset.SourceHash}.");
        return asset;
    }

    private static async Task<PbrSceneAsset> LoadPbrAsync(string? path, bool finest)
    {
        var timer = Stopwatch.StartNew();
#if BROWSER
        var bytes = await DownloadSceneAsync(path ?? throw new ArgumentException("The browser entry point must supply the published scene URL."));
        SetLoadingState("Preparing geometry and textures", double.NaN);
        await Task.Delay(20);
#else
        ReadOnlyMemory<byte> bytes = await File.ReadAllBytesAsync(path ?? Path.Combine(AppContext.BaseDirectory, "Assets", finest ? "BistroFinest.siapbr" : "Bistro.siapbr"));
#endif
        var readMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
#if BROWSER
        var scene = await BrowserOwner.RunCpuAsync(token => PbrSceneAsset.Decode(bytes.Span, 1024 * 1024 * 1024, token));
#else
        var scene = PbrSceneAsset.Decode(bytes.Span, 1024 * 1024 * 1024);
#endif
        Console.WriteLine(scene.Attribution);
        Console.WriteLine($"PBR scene: {bytes.Length} bytes; read {readMilliseconds:F2} ms, decode {timer.Elapsed.TotalMilliseconds:F2} ms; "
            + $"{scene.Geometry.Length} shared geometries, {scene.Materials.Length} materials, {scene.Instances.Length} instances.");
        return scene;
    }

    private static (ScenePipeline Pipeline, VisibilityDebugMode? DebugMode, float? Distance, string? ScenePath, bool Finest,
        (float3 Eye, float3 Target)? Camera) ParseOptions(string[] args)
    {
        var pipeline = ScenePipeline.Pbr;
        VisibilityDebugMode? debugMode = null;
        float? distance = null;
        string? scenePath = null;
        bool? finest = null;
        (float3 Eye, float3 Target)? camera = null;
        for (var i = 0; i < args.Length; i += 2) {
            if (i + 1 == args.Length) { throw new ArgumentException($"Missing value for {args[i]}."); }
            if (args[i] == "--pipeline") { pipeline = ParsePipeline(args[i + 1]); }
            else if (args[i] == "--scene") { scenePath = args[i + 1]; }
            else if (args[i] == "--gpu-timing") { GpuTiming = bool.Parse(args[i + 1]); }
            else if (args[i] == "--target-fps") {
                if (!int.TryParse(args[i + 1], out var fps) || fps is < 1 or > 1000) throw new ArgumentException("Expected --target-fps 1..1000.");
                TargetFps = fps; GpuTiming = true;
            }
            else if (args[i] == "--present") { ImmediatePresent = args[i + 1] switch {
                "fifo" => false, "immediate" => true, _ => throw new ArgumentException("Expected --present fifo|immediate.")
            }; }
            else if (args[i] == "--render-scale") {
                if (!float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
                    || !float.IsFinite(scale) || scale is < .0625f or > 1) throw new ArgumentException("Expected --render-scale 0.0625..1.");
                RenderScale = scale;
            }
            else if (args[i] is "--width" or "--height") {
                if (!int.TryParse(args[i + 1], out var size) || size is < 64 or > 8192) throw new ArgumentException("Expected --width/--height 64..8192.");
                if (args[i] == "--width") Width = size; else Height = size;
            }
            else if (args[i] == "--quality") { Quality = args[i + 1] switch {
                "low" => RenderQuality.Low, "medium" => RenderQuality.Medium, "high" => RenderQuality.High,
                _ => throw new ArgumentException("Expected --quality low|medium|high.")
            }; }
            else if (args[i] == "--benchmark-frames") {
                if (!int.TryParse(args[i + 1], out var frames) || frames is < 120 or > 10000) throw new ArgumentException("Expected --benchmark-frames 120..10000.");
                BenchmarkFrames = frames;
            }
            else if (args[i] == "--benchmark-motion") { BenchmarkMotion = bool.Parse(args[i + 1]); }
            else if (args[i] == "--camera") {
                var values = args[i + 1].Split(',');
                var numbers = new float[6];
                if (values.Length != numbers.Length) { throw new ArgumentException("Expected --camera x,y,z,targetX,targetY,targetZ."); }
                for (var j = 0; j < numbers.Length; j++) {
                    if (!float.TryParse(values[j], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[j])
                        || !float.IsFinite(numbers[j]) || MathF.Abs(numbers[j]) > 100000) {
                        throw new ArgumentException("Camera coordinates must be finite and within -100000..100000.");
                    }
                }
                var eye = new float3(numbers[0], numbers[1], numbers[2]);
                var target = new float3(numbers[3], numbers[4], numbers[5]);
                if (math.lengthsq(target - eye) < .000001f) { throw new ArgumentException("Camera eye and target must differ."); }
                camera = (eye, target);
            }
            else if (args[i] == "--lod") { finest = args[i + 1] switch {
                "auto" => false, "finest" => true, _ => throw new ArgumentException("Expected --lod auto|finest.")
            }; }
            else if (args[i] == "--debug") { debugMode = args[i + 1] switch {
                "shaded" => VisibilityDebugMode.Shaded,
                "triangles" => VisibilityDebugMode.Triangles,
                _ => throw new ArgumentException("Expected --debug shaded|triangles.")
            }; }
            else if (args[i] == "--distance") {
                if (!float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || !float.IsFinite(value) || value is < 0 or > 1) {
                    throw new ArgumentException("Expected --distance 0..1 (near to far; pauses the automatic tour).");
                }
                distance = value;
            }
            else { throw new ArgumentException("Unknown option: " + args[i]); }
        }
        if ((debugMode is not null || distance is not null) && pipeline == ScenePipeline.Unlit) {
            throw new ArgumentException("--debug and --distance require --pipeline bunny|pbr.");
        }
        if (RenderScale != 1 && pipeline != ScenePipeline.Pbr) {
            throw new ArgumentException("--render-scale requires --pipeline pbr.");
        }
        if (TargetFps != 0) {
            GpuTiming = true;
            if (scenePath?.Split('?')[0].EndsWith(".siastream", StringComparison.OrdinalIgnoreCase) == true)
                throw new ArgumentException("Dynamic resolution requires a monolithic PBR scene.");
        }
        var streamed = scenePath?.Split('?')[0].EndsWith(".siastream", StringComparison.OrdinalIgnoreCase) == true;
        if (GpuTiming && (pipeline != ScenePipeline.Pbr || streamed))
            throw new ArgumentException("--gpu-timing currently requires a monolithic PBR scene.");
#if BROWSER
        if (ImmediatePresent) throw new ArgumentException("Immediate present benchmarks are native-only.");
#endif
        if ((scenePath is not null || finest is not null || camera is not null) && pipeline != ScenePipeline.Pbr) {
            throw new ArgumentException("--scene, --lod and --camera require --pipeline pbr.");
        }
        if (finest is not null && scenePath?.Split('?')[0].EndsWith(".siastream", StringComparison.OrdinalIgnoreCase) == true) {
            throw new ArgumentException("--lod selects a monolithic scene mode; streamed scenes select resident detail automatically.");
        }
        return (pipeline, debugMode, distance, scenePath, finest ?? false, camera);
    }

    private static ScenePipeline ParsePipeline(string name) => name switch {
        "pbr" => ScenePipeline.Pbr,
        "unlit" => ScenePipeline.Unlit,
        "bunny" => ScenePipeline.Bunny,
        _ => throw new ArgumentException("Usage: --pipeline bunny|pbr|unlit")
    };
}
