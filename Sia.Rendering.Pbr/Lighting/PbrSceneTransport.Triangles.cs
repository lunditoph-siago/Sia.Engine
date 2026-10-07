using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public static partial class PbrSceneTransport
{
    private readonly record struct TransportSurface(float3 Albedo, float3 Emission, bool DoubleSided);

    private static TransportSurface Surface(PbrMaterialAsset material)
    {
        var metal = material.Parameters.Metallic * Average(material.MetallicRoughness).z;
        return new(material.Parameters.BaseColor * Average(material.BaseColor) * (1 - MathF.Min(1, MathF.Max(0, metal))),
            material.Parameters.EmissiveColor * material.Parameters.EmissiveStrength * Average(material.Emissive), material.DoubleSided);
    }

    private interface ITransportPosition<TVertex> where TVertex : unmanaged
    {
        static abstract float3 Get(in TVertex vertex);
    }

    private readonly struct MeshPosition : ITransportPosition<MeshVertex>
    {
        public static float3 Get(in MeshVertex vertex) => vertex.Position;
    }

    private readonly struct PackedPosition : ITransportPosition<float4>
    {
        public static float3 Get(in float4 vertex) => vertex.xyz;
    }

    private static TransportTriangles<MeshVertex, MeshPosition> Triangles(ReadOnlySpan<MeshVertex> vertices,
        ReadOnlySpan<uint> indices, float4x4 transform, TransportSurface surface) => new(vertices, indices, transform, surface);

    private static TransportTriangles<float4, PackedPosition> Triangles(ReadOnlySpan<float4> vertices,
        ReadOnlySpan<uint> indices, float4x4 transform, TransportSurface surface) => new(vertices, indices, transform, surface);

    // Stack-only traversal borrows the source; selection, destinations and budget effects stay at the call site.
    private ref struct TransportTriangles<TVertex, TPosition>(ReadOnlySpan<TVertex> vertices,
        ReadOnlySpan<uint> indices, float4x4 transform, TransportSurface surface)
        where TVertex : unmanaged
        where TPosition : struct, ITransportPosition<TVertex>
    {
        private readonly ReadOnlySpan<TVertex> _vertices = vertices;
        private readonly ReadOnlySpan<uint> _indices = indices;
        private int _offset;
        public SceneTraceTriangle Current { get; private set; }
        public TransportTriangles<TVertex, TPosition> GetEnumerator() => this;

        public bool MoveNext()
        {
            while (_offset < _indices.Length) {
                var a = math.mul(transform, new float4(TPosition.Get(in _vertices[(int)_indices[_offset]]), 1)).xyz;
                var b = math.mul(transform, new float4(TPosition.Get(in _vertices[(int)_indices[_offset + 1]]), 1)).xyz;
                var c = math.mul(transform, new float4(TPosition.Get(in _vertices[(int)_indices[_offset + 2]]), 1)).xyz;
                _offset += 3;
                if (math.lengthsq(math.cross(b - a, c - a)) < 1e-16f) continue;
                Current = new(a, b, c, surface.Albedo, surface.Emission, surface.DoubleSided);
                return true;
            }
            return false;
        }
    }
}
