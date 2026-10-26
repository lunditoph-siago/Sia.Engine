using Sia.Engine.Rendering;

namespace Sia.Engine.Example;

internal sealed record SceneOptions
{
    internal ScenePipeline Pipeline { get; private init; }
    internal RenderQuality Quality { get; private init; }
    internal string? ScenePath { get; private init; }
    internal string? EnvironmentPath { get; private init; }
    internal string? ProbesPath { get; private init; }
    internal float ExposureCompensation { get; private init; }

    private SceneOptions() { }

    internal static SceneOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var pipeline = ScenePipeline.Bunny;
        RenderQuality? quality = null;
        float? exposure = null;
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
                        _ => throw new ArgumentException("Unsupported scene pipeline.")
                    };
                    break;
                case "--scene": scene = value; break;
                case "--environment": environment = value; break;
                case "--probes": probes = value; break;
                case "--exposure-compensation":
                    if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var stops)
                        || !float.IsFinite(stops) || stops is < -16 or > 16)
                        throw new ArgumentException("Expected --exposure-compensation stops in -16..16.");
                    exposure = stops;
                    break;
                case "--quality":
                    quality = value switch {
                        "low" => RenderQuality.Low,
                        "medium" => RenderQuality.Medium,
                        "high" => RenderQuality.High,
                        _ => throw new ArgumentException("Expected --quality low|medium|high.")
                    };
                    break;
                default: throw new ArgumentException($"Unknown option: {args[i]}.");
            }
        }
        if (pipeline == ScenePipeline.Unlit && (quality is not null || scene is not null || environment is not null || probes is not null || exposure is not null))
            throw new ArgumentException("Unlit does not use scene, quality or lighting assets.");
        return new() { Pipeline = pipeline, Quality = quality ?? (pipeline == ScenePipeline.Bunny ? RenderQuality.High : RenderQuality.Low),
            ScenePath = scene, EnvironmentPath = environment, ProbesPath = probes, ExposureCompensation = exposure ?? 0 };
    }
}
