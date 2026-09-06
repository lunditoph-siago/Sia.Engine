using System.Diagnostics;
using Sia.Engine.Mesh;

namespace Sia.Engine.Example;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
            var (pipeline, assetPath) = ParseOptions(args);
            var asset = pipeline == ScenePipeline.VisibilityLod ? await LoadPatchAssetAsync(assetPath) : null;
            using var app = new SceneExampleApp(pipeline, asset);
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

    private static async Task<MeshPatchAsset> LoadPatchAssetAsync(string? path)
    {
        var timer = Stopwatch.StartNew();
        byte[] bytes;
        if (path is null) {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("Sia.Engine.Example.terrain.siapatch")
                ?? throw new FileNotFoundException("The bundled terrain patch asset is missing.");
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

    private static (ScenePipeline Pipeline, string? Asset) ParseOptions(string[] args)
    {
        var pipeline = ScenePipeline.Pbr;
        string? asset = null;
        for (var i = 0; i < args.Length; i += 2) {
            if (i + 1 == args.Length) { throw new ArgumentException("Expected --pipeline NAME or --asset PATH."); }
            if (args[i] == "--pipeline") { pipeline = ParsePipeline(args[i + 1]); }
            else if (args[i] == "--asset") { asset = args[i + 1]; }
            else { throw new ArgumentException("Unknown option: " + args[i]); }
        }
        if (asset is not null && pipeline != ScenePipeline.VisibilityLod) {
            throw new ArgumentException("--asset requires --pipeline visibility-lod.");
        }
        return (pipeline, asset);
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
