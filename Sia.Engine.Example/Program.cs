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
            ExampleContent.Entry? content = null;
            if (pipeline is ScenePipeline.Pbr or ScenePipeline.Bunny && scenePath is null) {
#if BROWSER
                throw new ArgumentException("The browser entry point must supply its default reference-scene URL.");
#else
                content = ExampleContent.Default(pipeline, Path.Combine(AppContext.BaseDirectory, "Assets"), options.Quality);
                scenePath = content.Scene;
#endif
            }
            await using var virtualUse = scenePath is not null && options.Quality != Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareStreamingAsync(scenePath, streamHttp) : null;
            PbrSceneFiles.Prepared? references = scenePath is null ? null : (PbrSceneFiles.Prepared?)virtualUse
                ?? await PbrSceneFiles.PrepareRasterAsync(scenePath, streamHttp);
            var lighting = await LightingFiles.PrepareAsync(options.EnvironmentPath ?? content?.Environment, options.ProbesPath, streamHttp);
            var lightmapsPath = content?.Lightmaps;
            await using var lightmaps = lightmapsPath is null ? null
                : await LightmapFiles.PrepareAsync(lightmapsPath, streamHttp, references!);
            var app = new SceneExampleApp(options, references, lighting, lightmaps);
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
                try { app.Run(); }
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
