using System.Diagnostics;
using System.Globalization;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
            var (pipeline, debugMode, distance, scenePath, finest, camera) = ParseOptions(args);
            var asset = pipeline == ScenePipeline.Bunny ? await LoadBunnyAsync() : null;
            var scene = pipeline == ScenePipeline.Pbr ? await LoadPbrAsync(scenePath) : null;
            using var app = new SceneExampleApp(pipeline, asset, debugMode, distance, scene, finest, camera);
#if BROWSER
            await app.RunAsync();
#else
            app.Run();
#endif
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception);
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
        var asset = MeshPatchAsset.Decode(bytes);
        Console.WriteLine($"Patch asset: {bytes.Length} bytes; read {readMilliseconds:F2} ms, decode/validate {timer.Elapsed.TotalMilliseconds:F2} ms; {asset.SourceHash}.");
        return asset;
    }

    private static async Task<PbrSceneAsset> LoadPbrAsync(string? path)
    {
        var timer = Stopwatch.StartNew();
#if BROWSER
        using var client = new HttpClient();
        var bytes = await client.GetByteArrayAsync(path ?? throw new ArgumentException("The browser entry point must supply the published scene URL."));
#else
        var bytes = await File.ReadAllBytesAsync(path ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Bistro.siapbr"));
#endif
        var scene = PbrSceneAsset.Decode(bytes, 512 * 1024 * 1024);
        Console.WriteLine(scene.Attribution);
        Console.WriteLine($"PBR scene: {bytes.Length} bytes; load/decode {timer.Elapsed.TotalMilliseconds:F2} ms; "
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
        if ((scenePath is not null || finest is not null || camera is not null) && pipeline != ScenePipeline.Pbr) {
            throw new ArgumentException("--scene, --lod and --camera require --pipeline pbr.");
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
