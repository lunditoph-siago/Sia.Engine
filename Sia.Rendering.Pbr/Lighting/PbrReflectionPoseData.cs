using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

/// <summary>Runtime GPU pose rows: full UUID/state, current/previous transforms and current inverse; not a persisted asset format.</summary>
public static class PbrReflectionPoseData
{
    public const int RowsPerPose = 14;
    public static void Pack(ReadOnlySpan<PbrReflectionInstancePose> current, ReadOnlySpan<PbrReflectionInstancePose> previous, Span<uint4> destination)
    {
        if (current.Length > PbrSceneData.MaximumInstances || previous.Length != current.Length || destination.Length != current.Length * RowsPerPose)
            throw new ArgumentException("Pose tables require the same bounded instance domain and exact caller storage.");
        Span<byte> id = stackalloc byte[16];
        for (var at = 0; at < current.Length; at++) {
            var now = current[at]; var then = previous[at];
            if (now.Id == Guid.Empty || now.Id != then.Id || !PbrSceneData.ValidTransform(now.Transform) || !PbrSceneData.ValidTransform(then.Transform))
                throw new ArgumentException("Pose rows require matching nonempty authored IDs and finite affine values.");
            var rows = destination.Slice(at * RowsPerPose, RowsPerPose);
            now.Id.TryWriteBytes(id, bigEndian: true, out _);
            rows[0] = new(BinaryPrimitives.ReadUInt32LittleEndian(id), BinaryPrimitives.ReadUInt32LittleEndian(id[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(id[8..]), BinaryPrimitives.ReadUInt32LittleEndian(id[12..]));
            var rigid = PbrReflectionHistory.IsRigid(now.Transform) && PbrReflectionHistory.IsRigid(then.Transform);
            rows[1] = new(now.Enabled ? 1u : 0u, then.Enabled ? 1u : 0u, rigid ? 1u : 0u, 0);
            Matrix(now.Transform, rows[2..6]); Matrix(then.Transform, rows[6..10]);
            Matrix(rigid ? math.inverse(now.Transform) : float4x4.identity, rows[10..14]);
        }
    }

    private static void Matrix(float4x4 matrix, Span<uint4> destination)
    {
        ReadOnlySpan<float4> columns = [matrix.c0, matrix.c1, matrix.c2, matrix.c3];
        MemoryMarshal.Cast<float4, uint4>(columns).CopyTo(destination);
    }
}
