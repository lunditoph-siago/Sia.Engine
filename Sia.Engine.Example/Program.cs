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
            var (pipeline, assetPath, scene, debugMode, distance) = ParseOptions(args);
            var asset = pipeline == ScenePipeline.VisibilityLod ? await LoadPatchAssetAsync(assetPath, scene) : null;
            using var app = new SceneExampleApp(pipeline, asset, scene, debugMode, distance);
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

    private static async Task<MeshPatchAsset> LoadPatchAssetAsync(string? path, PatchScene scene)
    {
        var timer = Stopwatch.StartNew();
        byte[] bytes;
        if (path is null) {
            var name = scene.ToString().ToLowerInvariant();
            using var stream = typeof(Program).Assembly.GetManifestResourceStream($"Sia.Engine.Example.{name}.siapatch")
                ?? throw new FileNotFoundException($"The bundled {name} patch asset is missing.");
            if (scene == PatchScene.Bunny) {
                Console.WriteLine("Stanford Bunny: Stanford University Computer Graphics Laboratory; https://graphics.stanford.edu/data/3Dscanrep/");
            }
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            bytes = memory.ToArray();
        }
        else {
#if BROWSER
            using var client = new HttpClient();
            bytes = await client.GetByteArrayAsync(path);
#else
            bytes = await File.ReadAllBytesAsync(path);
#endif
        }
        var readMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var asset = MeshPatchAsset.Decode(bytes);
        Console.WriteLine($"Patch asset: {bytes.Length} bytes; read {readMilliseconds:F2} ms, decode/validate {timer.Elapsed.TotalMilliseconds:F2} ms; {asset.SourceHash}.");
        return asset;
    }

    private static (ScenePipeline Pipeline, string? Asset, PatchScene Scene, VisibilityDebugMode? DebugMode, float? Distance) ParseOptions(string[] args)
    {
        var pipeline = ScenePipeline.Pbr;
        string? asset = null;
        var scene = PatchScene.Terrain;
        VisibilityDebugMode? debugMode = null;
        float? distance = null;
        for (var i = 0; i < args.Length; i += 2) {
            if (i + 1 == args.Length) { throw new ArgumentException($"Missing value for {args[i]}."); }
            if (args[i] == "--pipeline") { pipeline = ParsePipeline(args[i + 1]); }
            else if (args[i] == "--asset") { asset = args[i + 1]; }
            else if (args[i] == "--scene") { scene = args[i + 1] switch {
                "terrain" => PatchScene.Terrain, "bunny" => PatchScene.Bunny, "plane" => PatchScene.Plane,
                _ => throw new ArgumentException("Expected --scene terrain|bunny|plane.")
            }; }
            else if (args[i] == "--debug") { debugMode = args[i + 1] switch {
                "shaded" => VisibilityDebugMode.Shaded,
                "triangles" => VisibilityDebugMode.Triangles, "normals" => VisibilityDebugMode.Normals,
                "uv" => VisibilityDebugMode.UV, "albedo" => VisibilityDebugMode.Albedo,
                _ => throw new ArgumentException("Expected --debug shaded|triangles|normals|uv|albedo.")
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
        if ((asset is not null || args.Contains("--scene") || debugMode is not null || distance is not null)
            && pipeline != ScenePipeline.VisibilityLod) {
            throw new ArgumentException("--asset, --scene, --debug and --distance require --pipeline visibility-lod.");
        }
        if (distance is not null && scene != PatchScene.Bunny) {
            throw new ArgumentException("--distance requires --scene bunny.");
        }
        return (pipeline, asset, scene, debugMode, distance);
    }

    private static ScenePipeline ParsePipeline(string name) => name switch {
        "pbr" => ScenePipeline.Pbr,
        "atmosphere" => ScenePipeline.Atmosphere,
        "unlit" => ScenePipeline.Unlit,
        "normals" => ScenePipeline.Normals,
        "visibility" => ScenePipeline.Visibility,
        "visibility-lod" => ScenePipeline.VisibilityLod,
        "visibility-normals" => ScenePipeline.VisibilityNormals,
        "visibility-uv" => ScenePipeline.VisibilityUV,
        _ => throw new ArgumentException("Usage: --pipeline pbr|atmosphere|unlit|normals|visibility|visibility-lod|visibility-normals|visibility-uv")
    };
}
