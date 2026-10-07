using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
#if !BROWSER
            if (args.Length > 0 && args[0] == "--bake-reflections") {
                await BakeReflectionCaptureAsync(args);
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
            if (args.Length is 6 or 8 or 10 && args[0] == "--bake-lightmaps" && args[2] == "--output"
                && args[4] == "--mapped-scene") {
                var (resolution, maximumResolution) = ParseLightmapResolutions(args, 6);
                BakeLightmaps(args[1], args[3], args[5], resolution, maximumResolution);
                return 0;
            }
            if (args.Length is 4 or 6 or 8 or 10 or 12 && args[0] == "--bake-lightmap-stream" && args[2] == "--output") {
                var (resolution, maximumResolution, traceBytes, conventional) = ParseLightmapStreamOptions(args);
                await BakeLightmapStreamAsync(args[1], args[3], resolution, maximumResolution, traceBytes, conventional);
                return 0;
            }
            if (args.Length is 4 or 6 && args[0] == "--cook-stream" && args[2] == "--output") {
                var conventional = args.Length == 6 ? ParseConventionalGeometry(args[4], args[5]) : [];
                await CookStreamAsync(args[1], args[3], conventional);
                return 0;
            }
#endif
#if BROWSER
            BrowserOwner = await BrowserThread.StartAsync();
#endif
            var (pipeline, debugMode, distance, scenePath, lodOverride, camera) = ParseOptions(args);
            using var streamHttp = new HttpClient();
#if !BROWSER
            var bundledDescription = Path.Combine(AppContext.BaseDirectory, "Assets", "Stream", "scene.siascene");
            if (pipeline == ScenePipeline.Pbr && scenePath is null && File.Exists(bundledDescription))
                scenePath = bundledDescription;
#endif
            if (SceneInputs.IsDescription(scenePath)) {
                var inputs = await SceneInputs.LoadAsync(scenePath!, streamHttp);
                scenePath = inputs.SelectScene(Quality, lodOverride);
                EnvironmentPath ??= inputs.Environment;
                LightmapsPath ??= inputs.Lightmaps;
                ProbesPath ??= inputs.Probes;
                StaticTransportPath ??= inputs.StaticTransport;
                ReflectionsPath ??= inputs.Reflections;
                Console.WriteLine($"Scene description selected: {Quality}; {(lodOverride.HasValue || Quality == RenderQuality.Low ? "resident" : "mixed stream")}; shared baked inputs.");
            }
            var finest = lodOverride ?? false;
            if (ReflectionsPath is not null && EnvironmentPath is null)
                throw new ArgumentException("--reflections requires its matching --environment.");
            if (EnvironmentPath is not null)
                BakedEnvironment = await LoadEnvironmentAsync(EnvironmentPath, streamHttp);
            if (ReflectionsPath is not null)
                BakedReflections = await LoadReflectionCaptureAsync(ReflectionsPath, streamHttp);
            if (ProbesPath is not null)
                BakedProbes = await LoadProbesAsync(ProbesPath, streamHttp);
            if (StaticTransportPath is not null && (DynamicSceneGi ?? PbrRendererSettings.ForQuality(Quality).DynamicSceneGi))
                StaticTransport = await LoadStaticTransportAsync(StaticTransportPath, streamHttp);
            var paged = LightmapsPath?.Split('?')[0].EndsWith(".sialmst", StringComparison.OrdinalIgnoreCase) == true;
            await using var lightmapStream = paged ? await OpenLightmapStreamAsync(LightmapsPath!, streamHttp) : null;
            StreamedLightmaps = lightmapStream;
            if (paged && ProbesPath is null)
                BakedProbes = await LoadLightmapProbesAsync(LightmapsPath!, streamHttp);
            if (LightmapsPath is not null && !paged)
                BakedLightmaps = await LoadLightmapsAsync(LightmapsPath, streamHttp);
            var asset = pipeline == ScenePipeline.Bunny ? await LoadBunnyAsync() : null;
            await using var streaming = asset is not null ? await OpenBunnyStreamAsync(asset)
                : scenePath is not null && scenePath.Split('?')[0].EndsWith(".siastream", StringComparison.OrdinalIgnoreCase)
                    ? await OpenStreamAsync(scenePath, streamHttp) : null;
            var scene = streaming?.Bootstrap
                ?? (pipeline == ScenePipeline.Pbr ? await LoadPbrAsync(scenePath, finest, streamHttp) : null);
            var app = new SceneExampleApp(pipeline, asset, debugMode, distance, scene, finest, camera, streaming);
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
        finally { StreamedLightmaps = null; StaticTransport = null; }
    }
}
