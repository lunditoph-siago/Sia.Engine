using System.Diagnostics;
using System.Globalization;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
            var (pipeline, debugMode, distance) = ParseOptions(args);
            var asset = pipeline == ScenePipeline.Bunny ? await LoadBunnyAsync() : null;
            var scene = pipeline == ScenePipeline.Pbr ? await LoadPbrAsync() : null;
            using var app = new SceneExampleApp(pipeline, asset, debugMode, distance, scene);
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

    private static async Task<PbrSceneAsset> LoadPbrAsync()
    {
        var timer = Stopwatch.StartNew();
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("Sia.Engine.Example.scene.siapbr")
            ?? throw new FileNotFoundException("The bundled PBR scene is missing.");
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var bytes = memory.ToArray();
        var scene = PbrSceneAsset.Decode(bytes);
        Console.WriteLine(scene.Attribution);
        Console.WriteLine($"PBR scene: {bytes.Length} bytes; load/decode {timer.Elapsed.TotalMilliseconds:F2} ms; "
            + $"{scene.Geometry.Length} shared geometries, {scene.Materials.Length} materials, {scene.Instances.Length} instances.");
        return scene;
    }

    private static (ScenePipeline Pipeline, VisibilityDebugMode? DebugMode, float? Distance) ParseOptions(string[] args)
    {
        var pipeline = ScenePipeline.Pbr;
        VisibilityDebugMode? debugMode = null;
        float? distance = null;
        for (var i = 0; i < args.Length; i += 2) {
            if (i + 1 == args.Length) { throw new ArgumentException($"Missing value for {args[i]}."); }
            if (args[i] == "--pipeline") { pipeline = ParsePipeline(args[i + 1]); }
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
        return (pipeline, debugMode, distance);
    }

    private static ScenePipeline ParsePipeline(string name) => name switch {
        "pbr" => ScenePipeline.Pbr,
        "unlit" => ScenePipeline.Unlit,
        "bunny" => ScenePipeline.Bunny,
        _ => throw new ArgumentException("Usage: --pipeline bunny|pbr|unlit")
    };
}
