using Sia.Engine.Rendering;

namespace Sia.Engine.Example;

internal sealed record SceneOptions
{
    internal ScenePipeline Pipeline { get; private init; }
    internal RenderQuality Quality { get; private init; }
    internal string? ContentDirectory { get; private init; }
    internal float ExposureCompensation { get; private init; }
    internal bool AutoExposure { get; private init; }
    internal Guid? Camera { get; private init; }

    private SceneOptions() { }

    internal static SceneOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var pipeline = ScenePipeline.Bunny;
        RenderQuality? quality = null;
        float? exposure = null;
        var autoExposure = false;
        Guid? camera = null;
        string? contentDirectory = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length;) {
            var option = args[i++];
            if (!seen.Add(option)) throw new ArgumentException($"Duplicate option: {option}.");
            if (option == "--auto-exposure") {
                autoExposure = true;
                continue;
            }
            if (i == args.Length) throw new ArgumentException($"Missing value for {option}.");
            var value = args[i++];
            switch (option) {
                case "--pipeline":
                    pipeline = value switch {
                        "bunny" => ScenePipeline.Bunny,
                        "unlit" => ScenePipeline.Unlit,
                        _ => throw new ArgumentException("Unsupported scene pipeline.")
                    };
                    break;
                case "--content-directory":
                    ArgumentException.ThrowIfNullOrWhiteSpace(value);
                    contentDirectory = value;
                    break;
                case "--camera":
                    if (!((value.Length == 32 && Guid.TryParseExact(value, "N", out var id))
                        || (value.Length == 36 && Guid.TryParseExact(value, "D", out id))) || id == Guid.Empty)
                        throw new ArgumentException("Expected --camera nonempty UUID in N or D format.");
                    camera = id;
                    break;
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
                default: throw new ArgumentException($"Unknown option: {option}.");
            }
        }
        if (pipeline == ScenePipeline.Unlit && (quality is not null || contentDirectory is not null || exposure is not null || autoExposure || camera is not null))
            throw new ArgumentException("Unlit does not use PBR scene or lighting options.");
        return new() { Pipeline = pipeline, Quality = quality ?? (pipeline == ScenePipeline.Bunny ? RenderQuality.High : RenderQuality.Low),
            ContentDirectory = contentDirectory, ExposureCompensation = exposure ?? 0, AutoExposure = autoExposure, Camera = camera };
    }
}
