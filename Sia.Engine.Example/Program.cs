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
            PbrSceneFiles.Prepared? references = null;
            if (scenePath is not null) references = options.Quality == Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareRasterAsync(scenePath, streamHttp)
                : await PbrSceneFiles.PrepareStreamingAsync(scenePath, streamHttp);
            var lighting = await LightingFiles.PrepareAsync(options.EnvironmentPath, options.ProbesPath, streamHttp);
            var app = new SceneExampleApp(options, references, lighting);
            references = null;
            lighting = null;
#if BROWSER
            try {
                SetLoadingState("Preparing graphics", double.NaN);
                await Task.Delay(20);
                await app.RunAsync();
            }
            finally {
                await BrowserOwner.RunGraphicsAsync(() => {
                    app.Dispose();
                    return Task.FromResult(0);
                });
            }
#else
            using (app) {
                app.Run();
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
