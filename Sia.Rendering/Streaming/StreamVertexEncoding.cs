using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering;

/// <summary>Stateless runtime vertex encoding; source assets and their decoded values stay unchanged.</summary>
public static class StreamVertexEncoding
{
    public const int Words = 13;

    public static uint PackDirection(float3 direction)
    {
        var scale = math.cmax(math.abs(direction));
        if (!math.all(math.isfinite(direction))) throw new ArgumentException("Vertex direction must be finite.", nameof(direction));
        var oct = float2.zero;
        if (scale > 0) {
            direction /= scale; direction /= math.csum(math.abs(direction)); oct = direction.xy;
            if (direction.z < 0) oct = new((1 - MathF.Abs(oct.y)) * (oct.x < 0 ? -1 : 1), (1 - MathF.Abs(oct.x)) * (oct.y < 0 ? -1 : 1));
        }
        var x = (short)System.Math.Clamp((int)MathF.Round(oct.x * 32767), -32767, 32767);
        var y = (short)System.Math.Clamp((int)MathF.Round(oct.y * 32767), -32767, 32767);
        return (uint)(ushort)x | ((uint)(ushort)y << 16);
    }

    /// <summary>Writes three UInt4 planes and one UInt32 metadata plane with the supplied vertex stride.</summary>
    public static void PackVertex(Span<uint> packed, int index, int stride, in MeshVertex vertex, float2 bake,
        bool hasBake, uint sourceInstance)
    {
        if (index < 0 || stride <= index || packed.Length < checked(stride * 12 + index + 1) || sourceInstance > 1u << 24)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (!math.all(math.isfinite(vertex.Position)) || !math.all(math.isfinite(vertex.Normal))
            || !math.all(math.isfinite(vertex.UV)) || !math.all(math.isfinite(vertex.Tangent)) || !math.all(math.isfinite(bake)))
            throw new ArgumentException("Transformed vertex values must be finite.", nameof(vertex));
        var at = index * 4;
        packed[at] = Bits(vertex.Position.x); packed[at + 1] = Bits(vertex.Position.y);
        packed[at + 2] = Bits(vertex.Position.z); packed[at + 3] = Bits(vertex.UV.x);
        var normal = PackVector(vertex.Normal);
        var tangent = PackVector(vertex.Tangent.xyz);
        at += stride * 4;
        packed[at] = normal.x; packed[at + 1] = normal.y;
        packed[at + 2] = Bits(vertex.UV.y); packed[at + 3] = Bits(bake.x);
        at += stride * 4;
        packed[at] = tangent.x; packed[at + 1] = tangent.y;
        packed[at + 2] = Bits(vertex.Tangent.w); packed[at + 3] = Bits(bake.y);
        packed[stride * 12 + index] = sourceInstance | (hasBake ? 0x80000000u : 0);
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static uint2 PackVector(float3 direction)
    {
        var bits = Bits(math.cmax(math.abs(direction)));
        if (bits == 0) return default;
        var exponent = (int)(bits >> 23);
        var power = exponent == 0 ? System.Numerics.BitOperations.Log2(bits) - 149 : exponent - 127;
        // A power-of-two Float32 scale needs only its high half. Tiny subnormal
        // powers use the sign bit as a tag and retain their single-bit index.
        var scale = exponent == 0 ? 0x8000u | (uint)(power + 149) : (uint)exponent << 7;
        // Admitted powers are -149..127, so this reciprocal is always a normal Float64.
        var inverse = BitConverter.UInt64BitsToDouble((ulong)(1022 - power) << 52) * 32767d;
        // Decoding doubles the mantissa before scaling. Do not round the largest
        // finite exponent up to the unrepresentable positive/negative 2^128.
        var limit = power == 127 ? 32766 : 32767;
        uint Component(float value) => (ushort)(short)System.Math.Clamp((int)System.Math.Round(value * inverse), -limit, limit);
        return new(Component(direction.x) | (Component(direction.y) << 16), Component(direction.z) | (scale << 16));
    }
}
