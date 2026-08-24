using Sia.Math;

namespace Sia.Engine.Light;

public record struct LightColor(float3 Color, float Intensity)
{
    public static LightColor White => new(new float3(1, 1, 1), 1.0f);
}
