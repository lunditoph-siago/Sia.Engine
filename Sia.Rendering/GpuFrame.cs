using Sia;

namespace Sia.Engine.Rendering;

public readonly record struct GpuFrame(World World, Entity Device, Entity Queue);
