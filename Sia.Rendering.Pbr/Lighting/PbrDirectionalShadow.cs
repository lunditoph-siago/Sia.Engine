namespace Sia.Engine.Rendering.Pbr;

internal static class PbrDirectionalShadow
{
    internal static void ValidateBias(float constant, float slope)
    {
        if (!float.IsFinite(constant) || constant < 0 || !float.IsFinite(slope) || slope < 0 || !float.IsFinite(constant + slope))
            throw new ArgumentException("Directional shadow receiver biases must be finite nonnegative world distances.");
    }
}
