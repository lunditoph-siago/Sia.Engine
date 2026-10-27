#if !BROWSER
using System.Text.Json.Serialization;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;

namespace Sia.Engine.Example;

internal static partial class ScenePack
{
    internal sealed record CameraEntry
    {
        public required Guid Id { get; init; }
        public required float[] Position { get; init; }
        public required float[] Rotation { get; init; }
        public required float FovRadians { get; init; }
        public required float Near { get; init; }
        public required float Far { get; init; }
        [JsonConverter(typeof(JsonStringEnumConverter<PbrCameraFovAxis>))]
        public PbrCameraFovAxis FovAxis { get; init; }
    }

    private static PbrSceneCamera[]? ReadCameras(Recipe recipe, CancellationToken cancellationToken)
    {
        if (recipe.Cameras is not { } entries) return null;
        if (entries.Length > PbrSceneData.MaximumCameras) throw new InvalidDataException("Scene camera count limit exceeded.");
        var result = new PbrSceneCamera[entries.Length];
        for (var index = 0; index < result.Length; index++) {
            cancellationToken.ThrowIfCancellationRequested(); var entry = entries[index];
            if (entry is null || entry.Position is not { Length: 3 } || entry.Rotation is not { Length: 4 })
                throw new InvalidDataException("Typed camera recipes require XYZ positions and XYZW quaternions.");
            result[index] = new(entry.Id, new(entry.Position[0], entry.Position[1], entry.Position[2]),
                new quaternion(entry.Rotation[0], entry.Rotation[1], entry.Rotation[2], entry.Rotation[3]),
                entry.FovRadians, entry.Near, entry.Far, entry.FovAxis);
        }
        try { _ = PbrSceneData.Create([], []).WithCameras(result); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid authored camera metadata.", error); }
        return result;
    }
}
#endif
