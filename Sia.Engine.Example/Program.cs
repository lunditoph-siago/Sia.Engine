using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
#if !BROWSER
            if (await RunOfflineAsync(args)) return 0;
#endif
            var options = SceneOptions.Parse(args);
            var pipeline = options.Pipeline;
            var scenePath = options.ScenePath;
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
            if (referencePath) references = options.Quality == Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareRasterAsync(scenePath!, streamHttp)
                : await PbrSceneFiles.PrepareVirtualAsync(scenePath!, streamHttp);
            var lighting = await LightingFiles.PrepareAsync(options.EnvironmentPath, options.ProbesPath, streamHttp);
            await using var streaming = scenePath is not null && scenePath.Split('?')[0].EndsWith(".siastream", StringComparison.OrdinalIgnoreCase)
                    ? await OpenStreamAsync(scenePath, streamHttp) : null;
            var scene = streaming?.Bootstrap
                ?? (pipeline == ScenePipeline.Pbr && references is null ? await LoadPbrAsync(scenePath, streamHttp) : null);
            var app = new SceneExampleApp(options, scene, streaming, references, lighting);
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
