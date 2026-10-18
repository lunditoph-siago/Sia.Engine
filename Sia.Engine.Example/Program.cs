using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
#if !BROWSER
            if (args.Length is 4 or 6 or 8 && args[0] == "--package-example-content" && args[2] == "--output") {
                var (browser, maximumBytes) = ContentPackageOptions(args);
                await ExampleContent.PackAsync(args[1], args[3], maximumBundleBytes: maximumBytes, browser: browser);
                Console.WriteLine($"Packaged example content: {Path.GetFullPath(args[3])}");
                return 0;
            }
            if (args.Length is 4 or 6 && args[0] == "--cook-content" && args[2] == "--output") {
                var sceneId = await ContentCook.CookAsync(args[1], args[3], maximumTotalBytes: OfflineTotalBytes(args));
                Console.WriteLine($"Cooked content {sceneId.Value:N}: {Path.GetFullPath(args[3])}");
                return 0;
            }
            if (args.Length == 4 && args[0] == "--pack-scene" && args[2] == "--output") {
                var sceneId = await ScenePack.PackAsync(args[1], args[3]);
                Console.WriteLine($"Packed scene {sceneId.Value:N}: {Path.GetFullPath(args[3])}");
                return 0;
            }
            if (args.Length == 4 && args[0] == "--cook-mesh" && args[2] == "--output") {
                var mesh = MeshCook.Cook(args[1], args[3]);
                Console.WriteLine($"Cooked Mesh {mesh.Value:N}: {Path.GetFullPath(args[3])}");
                return 0;
            }
            if (args.Length == 4 && args[0] == "--bake-environment" && args[2] == "--output") {
                BakeEnvironment(args[1], args[3]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--bake-probes" && args[2] == "--output") {
                BakeProbes(args[1], args[3]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--cook-stream" && args[2] == "--output") {
                await CookStreamAsync(args[1], args[3]);
                return 0;
            }
#endif
            var (pipeline, debugMode, distance, scenePath, finest, camera) = ParseOptions(args);
#if BROWSER
            BrowserOwner = await BrowserThread.StartAsync();
#endif
            using var streamHttp = new HttpClient();
            if (pipeline is ScenePipeline.Pbr or ScenePipeline.Bunny && scenePath is null) {
#if BROWSER
                throw new ArgumentException("The browser entry point must supply its default reference-scene URL.");
#else
                scenePath = ExampleContent.DefaultScene(pipeline, Path.Combine(AppContext.BaseDirectory, "Assets"));
#endif
            }
            var referencePath = pipeline is ScenePipeline.Pbr or ScenePipeline.Bunny
                && scenePath is not null && PbrSceneFiles.IsReferenceScene(scenePath);
            if (pipeline == ScenePipeline.Bunny && !referencePath)
                throw new ArgumentException("Bunny requires a cooked reference .siascene, not runtime cooking.");
            PbrSceneFiles.Prepared? references = null;
            if (referencePath) references = Quality == Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareRasterAsync(scenePath!, streamHttp)
                : await PbrSceneFiles.PrepareVirtualAsync(scenePath!, streamHttp);
            var lighting = await LightingFiles.PrepareAsync(EnvironmentPath, ProbesPath, streamHttp);
            await using var streaming = scenePath is not null && scenePath.Split('?')[0].EndsWith(".siastream", StringComparison.OrdinalIgnoreCase)
                    ? await OpenStreamAsync(scenePath, streamHttp) : null;
            var scene = streaming?.Bootstrap
                ?? (pipeline == ScenePipeline.Pbr && references is null ? await LoadPbrAsync(scenePath, finest, streamHttp) : null);
            var app = new SceneExampleApp(pipeline, debugMode, distance, scene, finest, camera, streaming, references, lighting);
            references = null;
            lighting = null;
            scene = null;
#if BROWSER
            try {
                SetLoadingState("Preparing graphics", double.NaN);
                await Task.Delay(20);
                await app.RunAsync();
            }
            finally {
                await app.StopSceneStreamingAsync();
                await BrowserOwner.RunGraphicsAsync(() => {
                    app.Dispose();
                    return Task.FromResult(0);
                });
            }
#else
            using (app) {
                try {
                    app.Run();
                }
                finally { await app.StopSceneStreamingAsync(); }
            }
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

#if !BROWSER
    private static (bool Browser, long MaximumBytes) ContentPackageOptions(string[] args)
    {
        var browser = false;
        var maximumBytes = 512L * 1024 * 1024;
        var options = new HashSet<string>(StringComparer.Ordinal);
        for (var at = 4; at < args.Length; at += 2) {
            if (!options.Add(args[at])) throw new ArgumentException("Duplicate content package option.");
            if (args[at] == "--target") {
                browser = args[at + 1] switch {
                    "browser" => true,
                    "native" => false,
                    _ => throw new ArgumentException("Expected --target native|browser.")
                };
            }
            else if (args[at] == "--maximum-total-bytes" && long.TryParse(args[at + 1],
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes) && bytes > 0)
                maximumBytes = bytes;
            else throw new ArgumentException("Expected --target native|browser or --maximum-total-bytes <positive integer>.");
        }
        return (browser, maximumBytes);
    }

    private static long OfflineTotalBytes(string[] args)
    {
        if (args.Length == 4) return 512L * 1024 * 1024;
        if (args[4] != "--maximum-total-bytes" || !long.TryParse(args[5],
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes) || bytes <= 0)
            throw new ArgumentException("Expected --maximum-total-bytes <positive integer>.");
        return bytes;
    }
#endif
}
