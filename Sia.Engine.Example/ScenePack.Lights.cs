#if !BROWSER
using System.Text.Json.Serialization;
using Sia.Engine.Rendering.Pbr;

namespace Sia.Engine.Example;

internal static partial class ScenePack
{
    internal sealed record LightEntry
    {
        public required Guid Id { get; init; }
        [JsonConverter(typeof(JsonStringEnumConverter<PbrSceneLightKind>))]
        public required PbrSceneLightKind Kind { get; init; }
        public required float[] Color { get; init; }
        public required float Intensity { get; init; }
        public required float[] Position { get; init; }
        public required float[] Direction { get; init; }
        public float Range { get; init; }
        public float InnerAngle { get; init; }
        public float OuterAngle { get; init; }
        public bool CastShadows { get; init; }
    }

    private static PbrSceneLight[]? ReadLights(Recipe recipe, CancellationToken cancellationToken)
    {
        if (recipe.Lights is not { } entries) return null;
        if (entries.Length > PbrSceneData.MaximumLights) throw new InvalidDataException("Scene light count limit exceeded.");
        var result = new PbrSceneLight[entries.Length];
        for (var index = 0; index < result.Length; index++) {
            cancellationToken.ThrowIfCancellationRequested(); var entry = entries[index];
            if (entry is null || entry.Color is not { Length: 3 } || entry.Position is not { Length: 3 }
                || entry.Direction is not { Length: 3 })
                throw new InvalidDataException("Typed light recipes require RGB colors, XYZ positions and XYZ directions.");
            result[index] = new(entry.Id, entry.Kind, new(entry.Color[0], entry.Color[1], entry.Color[2]), entry.Intensity,
                new(entry.Position[0], entry.Position[1], entry.Position[2]),
                new(entry.Direction[0], entry.Direction[1], entry.Direction[2]), entry.Range,
                entry.InnerAngle, entry.OuterAngle, entry.CastShadows);
        }
        try { _ = PbrSceneData.Create([], []).WithLights(result); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid authored light metadata.", error); }
        return result;
    }
}
#endif
