namespace Sia.Engine.Rendering.Pbr;

/// <summary>Absolute runtime visibility by stable authored ID; affects drawing and live transport, never asset data.</summary>
public readonly record struct PbrInstanceVisibility(Guid Id, bool Enabled);
