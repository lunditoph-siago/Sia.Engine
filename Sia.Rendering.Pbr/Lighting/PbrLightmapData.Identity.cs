using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapData
{
    // One declared integrator: deterministic Fibonacci directions, mean-material
    // sky/emission/one directional diffuse bounce, area-weighted chart-safe L1.
    // Page hashes and quantized outputs are intentionally outside this input identity.
    private string ComputeBakeIdentity(CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BakeSettings.Environment is null ? "SIALIGHTMAP-AREAL1-1"u8 : "SIALIGHTMAP-AREAL1-HDR1"u8);
        Span<byte> bytes = stackalloc byte[152];
        _ = Convert.FromHexString(SceneIdentity, bytes[..32], out _, out _); hash.AppendData(bytes[..32]);
        BakeSettings.Encode(bytes); hash.AppendData(bytes[..BakeSettings.InputBytes]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, _receivers.Length); hash.AppendData(bytes[..4]);
        foreach (var receiver in _receivers) {
            token.ThrowIfCancellationRequested();
            receiver.InstanceId.TryWriteBytes(bytes[..16], bigEndian: true, out _);
            _ = Convert.FromHexString(receiver.SourceHash, bytes.Slice(16, 32), out _, out _);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[48..], receiver.MaterialSlot);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[52..], receiver.Resolution);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[56..], receiver.Padding);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[60..], receiver.ChartCount);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[64..], receiver.FirstMip); hash.AppendData(bytes[..68]);
            foreach (var chart in _charts.AsSpan(receiver.FirstChart, receiver.ChartCount)) {
                token.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteInt32LittleEndian(bytes, chart.X); BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], chart.Y);
                BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], chart.Width); BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], chart.Height);
                hash.AppendData(bytes[..16]);
            }
        }
        token.ThrowIfCancellationRequested(); return Convert.ToHexString(hash.GetHashAndReset());
    }
}
