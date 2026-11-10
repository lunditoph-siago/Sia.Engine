namespace Sia.Engine.Example;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
            var options = SceneOptions.Parse(args);
            var pipeline = options.Pipeline;
#if BROWSER
            BrowserOwner = await BrowserThread.StartAsync();
#endif
            using var streamHttp = new HttpClient();
            ExampleContent.Entry? content = null;
            if (pipeline == ScenePipeline.Bunny) {
#if BROWSER
                var directory = options.ContentDirectory ?? throw new ArgumentException("The browser entry point must supply its content directory.");
#else
                var directory = options.ContentDirectory ?? Path.Combine(AppContext.BaseDirectory, "Assets");
#endif
                content = await ExampleContent.DefaultAsync(directory, streamHttp);
            }
            var scenePath = content?.Scene;
            var packs = await PbrSceneFiles.ReadPackIndexAsync(content?.PackIndex, streamHttp);
            var rasterInstances = content?.ExplicitRasterInstances is { Length: > 0 } explicitRaster ? explicitRaster.ToHashSet() : null;
            var preset = Rendering.Pbr.PbrRendererSettings.ForQuality(options.Quality);
            var mixed = scenePath is not null && options.Quality != Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareMixedAsync(scenePath, streamHttp, options.Quality, tracing: preset.DynamicSceneGi,
                    rasterInstances: rasterInstances, packIndex: packs) : null;
            var raster = scenePath is not null && options.Quality == Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareRasterStreamingAsync(scenePath, streamHttp, packIndex: packs) : null;
            await using var sceneSource = mixed?.Source ?? raster?.Source;
            PbrSceneFiles.Prepared? references = (PbrSceneFiles.Prepared?)mixed ?? raster;
            mixed = null;
            raster = null;
            var lighting = await LightingFiles.PrepareAsync(content?.Environment, streamHttp, displayTransform: content?.DisplayTransform);
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
                : await LightmapFiles.PrepareAsync(lightmapsPath, streamHttp, references!, environment: lighting.Environment);
            if (lightmaps is not null) lighting = LightmapFiles.Lighting(lightmaps, lighting);
            var app = new SceneExampleApp(options, references, lighting, lightmaps, captures);
            references = null;
            lighting = null;
#if BROWSER
            var errors = new List<Exception>();
            try {
                SetLoadingState("Preparing graphics", double.NaN);
                await Task.Delay(20);
                await app.RunAsync();
            }
            catch (Exception error) { errors.Add(error); }
            try {
                await BrowserOwner.RunGraphicsAsync(async () => {
                    try { await app.StopSceneStreamingAsync(); }
                    catch (Exception error) { errors.Add(error); }
                    try { app.Dispose(); }
                    catch (Exception error) { errors.Add(error); }
                    return 0;
                });
            }
            catch (Exception error) { errors.Add(error); }
#else
            var errors = new List<Exception>();
            try { app.Run(); }
            catch (Exception error) { errors.Add(error); }
            try { app.StopSceneStreaming(); }
            catch (Exception error) { errors.Add(error); }
            try { app.Dispose(); }
            catch (Exception error) { errors.Add(error); }
#endif
            if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count != 0) throw new AggregateException("Scene execution or shutdown did not complete.", errors);
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
