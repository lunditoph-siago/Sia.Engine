using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Pbr;

public readonly record struct PbrDrawItem(MeshHandle Mesh, int InstanceIndex);
