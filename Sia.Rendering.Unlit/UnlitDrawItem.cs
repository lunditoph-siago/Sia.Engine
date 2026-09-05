using Sia.Engine.Mesh;

namespace Sia.Engine.Rendering.Unlit;

internal readonly record struct UnlitDrawItem(MeshHandle Mesh, int InstanceIndex);
