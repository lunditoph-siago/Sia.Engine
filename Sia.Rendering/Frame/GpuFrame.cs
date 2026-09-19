using Sia;

namespace Sia.Engine.Rendering;

public readonly record struct GpuFrame(
    World MainWorld,
    World ResourceWorld,
    Entity Device,
    Entity Queue);
