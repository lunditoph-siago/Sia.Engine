using Sia.Engine.Camera;
using Sia.Engine.Lighting;
using Sia.Engine.Mesh;
using Sia;

namespace Sia.Engine.Rendering;

public readonly record struct GpuFrame(World World, Entity Device, Entity Queue);
