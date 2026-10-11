using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
#if !BROWSER
            if (args.Length is 4 or 6 && args[0] == "--package-example-content" && args[2] == "--output") {
                await ExampleContent.PackAsync(args[1], args[3], maximumBundleBytes: OfflineTotalBytes(args));
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
                Console.WriteLine($"Cooked Mesh {mesh.Id.Value:N}: {Path.GetFullPath(args[3])}");
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
#endif
#if BROWSER
            BrowserOwner = await BrowserThread.StartAsync();
#endif
            var (pipeline, debugMode, distance, scenePath, finest, camera) = ParseOptions(args);
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
            if (pipeline is ScenePipeline.Pbr or ScenePipeline.Bunny && !referencePath)
                throw new ArgumentException("Mesh scenes require a cooked reference .siascene with independent products.");
            PbrSceneFiles.Prepared? references = null;
            await using var virtualUse = referencePath && Quality != Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareStreamingAsync(scenePath!, streamHttp) : null;
            if (referencePath) references = Quality == Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareRasterAsync(scenePath!, streamHttp)
                : virtualUse;
            var lighting = await LightingFiles.PrepareAsync(EnvironmentPath, ProbesPath, streamHttp);
            var app = new SceneExampleApp(pipeline, debugMode, distance, finest, camera, references, lighting);
            references = null;
            lighting = null;
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
