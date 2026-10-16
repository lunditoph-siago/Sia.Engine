using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
#if !BROWSER
            if (args.Length == 4 && args[0] == "--bake-reflection-capture" && args[2] == "--output") {
                var capture = await ReflectionCaptureCook.CookAsync(args[1], args[3]);
                Console.WriteLine($"Baked reflection capture {capture.Id.Value:N}: {capture.Files.Count} files, {capture.EncodedBytes} bytes, {Path.GetFullPath(args[3])}");
                return 0;
            }
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
                var content = ExampleContent.Default(pipeline, Path.Combine(AppContext.BaseDirectory, "Assets"));
                scenePath = content.Scene;
                LightmapsPath ??= content.Lightmaps;
                EnvironmentPath ??= content.Environment;
                if (ReflectionCapturePaths.Count == 0) ReflectionCapturePaths.AddRange(content.ReflectionCaptures);
#endif
            }
            var referencePath = pipeline is ScenePipeline.Pbr or ScenePipeline.Bunny
                && scenePath is not null && PbrSceneFiles.IsReferenceScene(scenePath);
            if (pipeline is ScenePipeline.Pbr or ScenePipeline.Bunny && !referencePath)
                throw new ArgumentException("Mesh scenes require a cooked reference .siascene with independent products.");
            PbrSceneFiles.Prepared? references = null;
            var dynamicGi = DynamicSceneGi ?? PbrRendererSettings.ForQuality(Quality).DynamicSceneGi;
            var liveReflections = SceneReflections ?? (dynamicGi && PbrRendererSettings.ForQuality(Quality).Reflections is not null);
            if (liveReflections && (!dynamicGi || Quality == Rendering.RenderQuality.Low))
                throw new ArgumentException("Live reflections require the shared dynamic local-BVH transport and Visibility path.");
            await using var mixedUse = referencePath && Quality != Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareMixedAsync(scenePath!, streamHttp, Quality, tracing: dynamicGi,
                    rasterInstances: MovingInstance is { } moving ? new HashSet<Guid> { moving } : null) : null;
            if (referencePath) references = Quality == Rendering.RenderQuality.Low
                ? await PbrSceneFiles.PrepareRasterAsync(scenePath!, streamHttp)
                : mixedUse;
            var lighting = await LightingFiles.PrepareAsync(EnvironmentPath, ProbesPath, streamHttp);
            var captures = new ReflectionCaptureFiles.Prepared[ReflectionCapturePaths.Count];
            for (var at = 0; at < captures.Length; at++) {
                captures[at] = await ReflectionCaptureFiles.PrepareAsync(ReflectionCapturePaths[at], streamHttp, references!);
                if (lighting.Environment is null) lighting = lighting with { Environment = captures[at].Source };
                if (ReflectionCaptureFiles.EnvironmentHash(lighting.Environment.Data) != captures[at].Capture.Data.SourceEnvironment.ContentHash)
                    throw new ArgumentException("Selected reflection captures require the same source environment as scene lighting.");
            }
            await using var lightmaps = LightmapsPath is null ? null
                : await LightmapFiles.PrepareAsync(LightmapsPath, streamHttp, references!);
            if (MovingInstance is { } movingId) {
                var found = false;
                foreach (var instance in references!.Scene.Data.Instances) found |= instance.Id == movingId;
                if (!found) throw new ArgumentException("Moving instance is absent from the selected scene.");
                if (lightmaps is not null)
                    foreach (var receiver in lightmaps.Set.Data.Receivers)
                        if (receiver.Receiver.InstanceId == movingId)
                            throw new ArgumentException("A static lightmap receiver cannot be the moving instance.");
                foreach (var capture in captures)
                    if (capture.Capture.Data.StaticInstances.Contains(movingId))
                        throw new ArgumentException("A static capture instance cannot be the moving instance.");
            }
            var app = new SceneExampleApp(pipeline, debugMode, distance, finest, camera, references, lighting, lightmaps, captures);
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
