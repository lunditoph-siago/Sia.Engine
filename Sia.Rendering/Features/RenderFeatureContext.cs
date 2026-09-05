namespace Sia.Engine.Rendering;

public readonly record struct RenderFeatureContext<TContext>(
    RenderWorld RenderWorld,
    RenderView View,
    TContext Frame);
