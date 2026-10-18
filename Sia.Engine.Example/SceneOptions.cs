using Sia.Engine.Rendering;

namespace Sia.Engine.Example;

internal sealed record SceneOptions
{
    internal ScenePipeline Pipeline { get; private init; }
    internal RenderQuality Quality { get; private init; }
    internal string? ScenePath { get; private init; }
    internal string? EnvironmentPath { get; private init; }
    internal string? ProbesPath { get; private init; }

    private SceneOptions() { }

    internal static SceneOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
#if BROWSER
        var pipeline = ScenePipeline.Bunny;
#else
        var pipeline = ScenePipeline.Pbr;
#endif
        RenderQuality? quality = null;
        string? scene = null, environment = null, probes = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2) {
            if (i + 1 == args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
            if (!seen.Add(args[i])) throw new ArgumentException($"Duplicate option: {args[i]}.");
            var value = args[i + 1];
            switch (args[i]) {
                case "--pipeline":
                    pipeline = value switch {
                        "bunny" => ScenePipeline.Bunny,
                        "unlit" => ScenePipeline.Unlit,
#if !BROWSER
                        "pbr" => ScenePipeline.Pbr,
#endif
                        _ => throw new ArgumentException("Unsupported scene pipeline.")
                    };
                    break;
                case "--scene": scene = value; break;
                case "--environment": environment = value; break;
                case "--probes": probes = value; break;
#if !BROWSER
                case "--quality":
                    quality = value switch {
                        "low" => RenderQuality.Low,
                        "medium" => RenderQuality.Medium,
                        "high" => RenderQuality.High,
                        _ => throw new ArgumentException("Expected --quality low|medium|high.")
                    };
                    break;
#endif
                default: throw new ArgumentException($"Unknown option: {args[i]}.");
            }
        }
        if (pipeline == ScenePipeline.Unlit && (quality is not null || scene is not null || environment is not null || probes is not null))
            throw new ArgumentException("Unlit does not use scene, quality or lighting assets.");
#if BROWSER
        quality = pipeline == ScenePipeline.Bunny ? RenderQuality.High : RenderQuality.Low;
#endif
        return new() { Pipeline = pipeline, Quality = quality ?? RenderQuality.Low,
            ScenePath = scene, EnvironmentPath = environment, ProbesPath = probes };
    }
}
