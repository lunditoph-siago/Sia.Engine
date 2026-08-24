using Sia.Math;

namespace Sia.Engine.Mesh;

public sealed record MeshData(MeshVertex[] Vertices, uint[] Indices, Aabb Bounds);
