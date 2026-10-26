namespace Sia.Engine.Lighting;

/// <summary>Inverse-square point light. Range zero means no distance cutoff; positive values add a finite fade.</summary>
public record struct PointLight(float Range);
