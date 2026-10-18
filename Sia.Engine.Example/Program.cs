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
            var preset = Rendering.Pbr.PbrRendererSettings.ForQuality(options.Quality);
            await using var mixed = scenePath is not null && options.Quality != Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareMixedAsync(scenePath, streamHttp, options.Quality, tracing: preset.DynamicSceneGi) : null;
            PbrSceneFiles.Prepared? references = scenePath is null ? null : (PbrSceneFiles.Prepared?)mixed
                ?? await PbrSceneFiles.PrepareRasterAsync(scenePath, streamHttp);
            var lighting = await LightingFiles.PrepareAsync(options.EnvironmentPath ?? content?.Environment, options.ProbesPath, streamHttp);
            var capturePaths = content?.ReflectionCaptures ?? [];
            var captures = new ReflectionCaptureFiles.Prepared[capturePaths.Length];
            for (var at = 0; at < captures.Length; at++) {
                captures[at] = await ReflectionCaptureFiles.PrepareAsync(capturePaths[at], streamHttp, references!);
                if (lighting.Environment is null) lighting = lighting with { Environment = captures[at].Source };
                if (ReflectionCaptureFiles.EnvironmentHash(lighting.Environment.Data) != captures[at].Capture.Data.SourceEnvironment.ContentHash)
                    throw new ArgumentException("Selected reflection captures require the same source environment as scene lighting.");
            }
            var lightmapsPath = content?.Lightmaps;
            await using var lightmaps = lightmapsPath is null ? null
                : await LightmapFiles.PrepareAsync(lightmapsPath, streamHttp, references!);
            var app = new SceneExampleApp(options, references, lighting, lightmaps, captures);
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
