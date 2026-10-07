using System.Globalization;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

public static partial class Program
{
    internal static int BenchmarkFrames { get; private set; }
    internal static bool BenchmarkMotion { get; private set; }
    internal static RenderQuality Quality { get; private set; } = RenderQuality.Low;
    internal static float RenderScale { get; private set; } = 1;
    internal static int Width { get; private set; } = 1280;
    internal static int Height { get; private set; } = 720;
    internal static bool ImmediatePresent { get; private set; }
    internal static bool GpuTiming { get; private set; }
    internal static bool? GpuTraversal { get; private set; }
    internal static PbrOpaquePath? OpaquePath { get; private set; }
    internal static bool ExportSurfaceData { get; private set; }
    internal static IblEnvironmentAsset? BakedEnvironment { get; private set; }
    internal static PbrReflectionCaptureAsset? BakedReflections { get; private set; }
    internal static DiffuseProbeAsset? BakedProbes { get; private set; }
    internal static SceneTraceData? StaticTransport { get; private set; }
    internal static PbrLightmapAsset? BakedLightmaps { get; private set; }
    internal static PbrLightmapStream? StreamedLightmaps { get; private set; }
    internal static bool? DynamicSceneGi { get; private set; }
    internal static string? ProbesPath { get; private set; }
    internal static string? StaticTransportPath { get; private set; }
    internal static string? LightmapsPath { get; private set; }
    internal static string? EnvironmentPath { get; private set; }
    internal static string? ReflectionsPath { get; private set; }
    internal static int TargetFps { get; private set; }

    private static (ScenePipeline Pipeline, VisibilityDebugMode? DebugMode, float? Distance, string? ScenePath, bool? Finest,
        (float3 Eye, float3 Target)? Camera) ParseOptions(string[] args)
    {
        var pipeline = ScenePipeline.Pbr;
        VisibilityDebugMode? debugMode = null;
        float? distance = null;
        string? scenePath = null;
        bool? finest = null;
        (float3 Eye, float3 Target)? camera = null;
        for (var i = 0; i < args.Length; i += 2) {
            if (i + 1 == args.Length) {
                throw new ArgumentException($"Missing value for {args[i]}.");
            }
            if (args[i] == "--pipeline") {
                pipeline = ParsePipeline(args[i + 1]);
            }
            else if (args[i] == "--scene") {
                scenePath = args[i + 1];
            }
            else if (args[i] == "--pbr-path") {
                OpaquePath = args[i + 1] switch {
                    "visibility" => PbrOpaquePath.Visibility,
                    "forward" => PbrOpaquePath.ForwardPlus,
                    _ => throw new ArgumentException("Expected --pbr-path visibility|forward.")
                };
            }
            else if (args[i] == "--environment") {
                EnvironmentPath = args[i + 1];
            }
            else if (args[i] == "--reflections") {
                ReflectionsPath = args[i + 1];
            }
            else if (args[i] == "--probes") {
                ProbesPath = args[i + 1];
            }
            else if (args[i] == "--static-transport") {
                StaticTransportPath = args[i + 1];
            }
            else if (args[i] == "--lightmaps") {
                LightmapsPath = args[i + 1];
            }
            else if (args[i] == "--scene-gi") {
                DynamicSceneGi = bool.Parse(args[i + 1]);
            }
            else if (args[i] == "--gpu-timing") {
                GpuTiming = bool.Parse(args[i + 1]);
            }
            else if (args[i] == "--gpu-traversal") {
                GpuTraversal = bool.Parse(args[i + 1]);
            }
            else if (args[i] == "--surface-data") {
                ExportSurfaceData = bool.Parse(args[i + 1]);
            }
            else if (args[i] == "--target-fps") {
                if (!int.TryParse(args[i + 1], out var fps) || fps is < 1 or > 1000)
                    throw new ArgumentException("Expected --target-fps 1..1000.");
                TargetFps = fps;
            }
            else if (args[i] == "--present") {
                ImmediatePresent = args[i + 1] switch {
                    "fifo" => false,
                    "immediate" => true,
                    _ => throw new ArgumentException("Expected --present fifo|immediate.")
                };
            }
            else if (args[i] == "--render-scale") {
                if (!float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
                    || !float.IsFinite(scale) || scale is < .0625f or > 1)
                    throw new ArgumentException("Expected --render-scale 0.0625..1.");
                RenderScale = scale;
            }
            else if (args[i] is "--width" or "--height") {
                if (!int.TryParse(args[i + 1], out var size) || size is < 64 or > 8192)
                    throw new ArgumentException("Expected --width/--height 64..8192.");
                if (args[i] == "--width")
                    Width = size;
                else
                    Height = size;
            }
            else if (args[i] == "--quality") {
                Quality = args[i + 1] switch {
                    "low" => RenderQuality.Low,
                    "medium" => RenderQuality.Medium,
                    "high" => RenderQuality.High,
                    _ => throw new ArgumentException("Expected --quality low|medium|high.")
                };
            }
            else if (args[i] == "--benchmark-frames") {
                if (!int.TryParse(args[i + 1], out var frames) || frames is < 120 or > 10000)
                    throw new ArgumentException("Expected --benchmark-frames 120..10000.");
                BenchmarkFrames = frames;
            }
            else if (args[i] == "--benchmark-motion") {
                BenchmarkMotion = bool.Parse(args[i + 1]);
            }
            else if (args[i] == "--camera") {
                var values = args[i + 1].Split(',');
                var numbers = new float[6];
                if (values.Length != numbers.Length) {
                    throw new ArgumentException("Expected --camera x,y,z,targetX,targetY,targetZ.");
                }
                for (var j = 0; j < numbers.Length; j++) {
                    if (!float.TryParse(values[j], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[j])
                        || !float.IsFinite(numbers[j]) || MathF.Abs(numbers[j]) > 100000) {
                        throw new ArgumentException("Camera coordinates must be finite and within -100000..100000.");
                    }
                }
                var eye = new float3(numbers[0], numbers[1], numbers[2]);
                var target = new float3(numbers[3], numbers[4], numbers[5]);
                if (math.lengthsq(target - eye) < .000001f) {
                    throw new ArgumentException("Camera eye and target must differ.");
                }
                camera = (eye, target);
            }
            else if (args[i] == "--lod") {
                finest = args[i + 1] switch {
                    "auto" => false,
                    "finest" => true,
                    _ => throw new ArgumentException("Expected --lod auto|finest.")
                };
            }
            else if (args[i] == "--debug") {
                debugMode = args[i + 1] switch {
                    "shaded" => VisibilityDebugMode.Shaded,
                    "triangles" => VisibilityDebugMode.Triangles,
                    "normals" => VisibilityDebugMode.Normals,
                    "albedo" => VisibilityDebugMode.Albedo,
                    _ => throw new ArgumentException("Expected --debug shaded|triangles|normals|albedo.")
                };
            }
            else if (args[i] == "--distance") {
                if (!float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || !float.IsFinite(value) || value is < 0 or > 1) {
                    throw new ArgumentException("Expected --distance 0..1 (near to far; pauses the automatic tour).");
                }
                distance = value;
            }
            else {
                throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if (distance is not null && pipeline != ScenePipeline.Bunny) {
            throw new ArgumentException("--distance requires --pipeline bunny; use --camera for PBR scenes.");
        }
        if (debugMode is not null && pipeline == ScenePipeline.Unlit) {
            throw new ArgumentException("--debug requires --pipeline bunny|pbr.");
        }
        if (pipeline != ScenePipeline.Pbr) {
            for (var i = 0; i < args.Length; i += 2) {
                if (args[i] is "--quality" or "--pbr-path" or "--environment" or "--probes" or "--lightmaps" or "--reflections"
                    or "--scene-gi" or "--surface-data"
                    or "--render-scale" or "--target-fps") {
                    throw new ArgumentException($"{args[i]} requires --pipeline pbr.");
                }
            }
        }
        if (pipeline == ScenePipeline.Unlit && args.Contains("--gpu-timing")) {
            throw new ArgumentException("--gpu-timing requires --pipeline bunny|pbr.");
        }
        if (pipeline == ScenePipeline.Unlit && args.Contains("--gpu-traversal")) {
            throw new ArgumentException("--gpu-traversal requires --pipeline bunny|pbr.");
        }
        if (TargetFps != 0) {
            GpuTiming = true;
        }
#if BROWSER
        if (ImmediatePresent) throw new ArgumentException("Immediate present benchmarks are native-only.");
#endif
        if ((scenePath is not null || finest is not null || camera is not null) && pipeline != ScenePipeline.Pbr) {
            throw new ArgumentException("--scene, --lod and --camera require --pipeline pbr.");
        }
        if (finest is not null && scenePath?.Split('?')[0].EndsWith(".siastream", StringComparison.OrdinalIgnoreCase) == true) {
            throw new ArgumentException("--lod selects a monolithic scene mode; streamed scenes select resident detail automatically.");
        }
        return (pipeline, debugMode, distance, scenePath, finest, camera);
    }

    private static ScenePipeline ParsePipeline(string name) => name switch {
        "pbr" => ScenePipeline.Pbr,
        "unlit" => ScenePipeline.Unlit,
        "bunny" => ScenePipeline.Bunny,
        _ => throw new ArgumentException("Usage: --pipeline bunny|pbr|unlit")
    };
}
