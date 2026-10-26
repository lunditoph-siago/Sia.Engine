using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public enum PbrCameraFovAxis { Vertical, Horizontal }

/// <summary>A static rigid world-space camera. Local forward is -Z and up is +Y; FOV is in radians.</summary>
public readonly record struct PbrSceneCamera(Guid Id, float3 Position, quaternion Rotation, float FovRadians,
    float Near, float Far, PbrCameraFovAxis FovAxis = PbrCameraFovAxis.Vertical)
{
    /// <summary>Resolves the authored gate against a finite positive viewport aspect, without changing the stored value.</summary>
    public float GetVerticalFov(float aspect)
    {
        if (!float.IsFinite(aspect) || aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect));
        if (!float.IsFinite(FovRadians) || FovRadians <= 0 || FovRadians >= MathF.PI)
            throw new ArgumentException("Camera FOV must be finite and within (0, pi).");
        var result = FovRadians;
        if (FovAxis == PbrCameraFovAxis.Horizontal) {
            // Reject overflow before atan can turn it into an apparently valid finite FOV.
            var tangent = MathF.Tan(FovRadians * .5f) / aspect;
            if (!float.IsFinite(tangent) || tangent <= 0)
                throw new ArgumentException("Camera gate cannot be represented at this aspect.");
            result = 2 * MathF.Atan(tangent);
        }
        else if (FovAxis != PbrCameraFovAxis.Vertical) throw new ArgumentException("Unknown camera FOV axis.");
        if (!float.IsFinite(result) || result <= 0 || result >= MathF.PI)
            throw new ArgumentException("Camera gate cannot be represented at this aspect.");
        var cotangent = 1 / MathF.Tan(result * .5f);
        if (!float.IsFinite(cotangent) || cotangent <= 0 || !float.IsFinite(cotangent / aspect) || cotangent / aspect <= 0
            || !float.IsFinite(1 / cotangent) || !float.IsFinite(1 / (cotangent / aspect)))
            throw new ArgumentException("Camera perspective scale cannot be represented at this aspect.");
        return result;
    }
}

public sealed partial class PbrSceneData
{
    private static void ValidateCameras(ReadOnlySpan<PbrSceneCamera> cameras)
    {
        if (cameras.IsEmpty) return;
        if (cameras.Length > MaximumCameras) throw new ArgumentException("Scene camera count limit exceeded.");
        var ids = new HashSet<Guid>();
        foreach (var camera in cameras) {
            var squared = math.lengthsq(camera.Rotation.value);
            if (camera.Id == Guid.Empty || !ids.Add(camera.Id)
                || !math.all(math.isfinite(camera.Position)) || !math.all(math.isfinite(camera.Rotation.value))
                || !float.IsFinite(squared) || MathF.Abs(squared - 1) > 1e-5f
                || !float.IsFinite(camera.FovRadians) || camera.FovRadians <= 0 || camera.FovRadians >= MathF.PI
                || !float.IsFinite(camera.Near) || !float.IsFinite(camera.Far) || camera.Near <= 0 || camera.Far <= camera.Near
                || camera.FovAxis is < PbrCameraFovAxis.Vertical or > PbrCameraFovAxis.Horizontal)
                throw new ArgumentException("Cameras require unique nonempty IDs, finite rigid poses, near-unit quaternions and finite perspective gates/planes.");
            var projection = float4x4.PerspectiveFov(camera.GetVerticalFov(1), 1, camera.Near, camera.Far);
            var inverse = math.inverse(projection);
            if (!math.all(math.isfinite(projection.c0)) || !math.all(math.isfinite(projection.c1))
                || !math.all(math.isfinite(projection.c2)) || !math.all(math.isfinite(projection.c3)) || projection.c3.z >= 0
                || !math.all(math.isfinite(inverse.c0)) || !math.all(math.isfinite(inverse.c1))
                || !math.all(math.isfinite(inverse.c2)) || !math.all(math.isfinite(inverse.c3)))
                throw new ArgumentException("Camera perspective planes exceed finite nondegenerate Float32 projection admission.");
        }
    }
}
