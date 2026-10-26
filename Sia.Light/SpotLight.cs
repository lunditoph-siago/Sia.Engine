namespace Sia.Engine.Lighting;

/// <summary>Inverse-square cone light. Range zero means no distance cutoff; angles are in radians.</summary>
public record struct SpotLight(float Range, float InnerAngle, float OuterAngle);
