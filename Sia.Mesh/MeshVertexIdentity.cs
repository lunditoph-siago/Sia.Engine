using Sia.Math;

namespace Sia.Engine.Mesh;

// Explicit meaningful attributes; MeshVertex's memory padding is not vertex identity.
internal readonly record struct MeshVertexIdentity(float X, float Y, float Z, float Nx, float Ny, float Nz,
    float U, float V, float Tx, float Ty, float Tz, float Tw, float BakeU, float BakeV)
{
    internal static MeshVertexIdentity Create(MeshVertex v, float2 bakeUv = default) => new(
        v.Position.x, v.Position.y, v.Position.z, v.Normal.x, v.Normal.y, v.Normal.z,
        v.UV.x, v.UV.y, v.Tangent.x, v.Tangent.y, v.Tangent.z, v.Tangent.w, bakeUv.x, bakeUv.y);
}
