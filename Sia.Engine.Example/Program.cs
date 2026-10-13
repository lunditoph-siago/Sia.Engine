using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
#if !BROWSER
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
#if BROWSER
            BrowserOwner = await BrowserThread.StartAsync();
#endif
            var (pipeline, debugMode, distance, scenePath, finest, camera) = ParseOptions(args);
            using var streamHttp = new HttpClient();
            var referencePath = pipeline == ScenePipeline.Pbr && scenePath is not null && PbrSceneFiles.IsReferenceScene(scenePath);
            PbrSceneFiles.Prepared? references = null;
            if (referencePath) references = Quality == Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareRasterAsync(scenePath!, streamHttp)
                : await PbrSceneFiles.PrepareVirtualAsync(scenePath!, streamHttp);
            var lighting = await LightingFiles.PrepareAsync(EnvironmentPath, ProbesPath, streamHttp);
            var asset = pipeline == ScenePipeline.Bunny ? await LoadBunnyAsync() : null;
            await using var streaming = asset is not null ? await OpenBunnyStreamAsync(asset)
                : scenePath is not null && scenePath.Split('?')[0].EndsWith(".siastream", StringComparison.OrdinalIgnoreCase)
                    ? await OpenStreamAsync(scenePath, streamHttp) : null;
            var scene = streaming?.Bootstrap
                ?? (pipeline == ScenePipeline.Pbr && references is null ? await LoadPbrAsync(scenePath, finest, streamHttp) : null);
            var app = new SceneExampleApp(pipeline, asset, debugMode, distance, scene, finest, camera, streaming, references, lighting);
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
}
