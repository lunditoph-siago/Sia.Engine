using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapData
{
    public void Save(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[204]; "SIALMIDX"u8.CopyTo(header);
        ProductId.Value.TryWriteBytes(header.Slice(8, 16), bigEndian: true, out _);
        _ = Convert.FromHexString(SceneIdentity, header.Slice(24, 32), out _, out _);
        _ = Convert.FromHexString(BakeIdentity, header.Slice(56, 32), out _, out _);
        BinaryPrimitives.WriteInt32LittleEndian(header[88..], _receivers.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[92..], _pages.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[96..], _charts.Length);
        BakeSettings.Encode(header[100..]);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header); stream.Write(header);
        Span<byte> row = stackalloc byte[120];
        foreach (var receiver in _receivers) {
            cancellationToken.ThrowIfCancellationRequested();
            receiver.Encode(row);
            hash.AppendData(row); stream.Write(row);
        }
        foreach (var chart in _charts) {
            cancellationToken.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt32LittleEndian(row, chart.Receiver);
            BinaryPrimitives.WriteInt32LittleEndian(row[4..], chart.X); BinaryPrimitives.WriteInt32LittleEndian(row[8..], chart.Y);
            BinaryPrimitives.WriteInt32LittleEndian(row[12..], chart.Width); BinaryPrimitives.WriteInt32LittleEndian(row[16..], chart.Height);
            BinaryPrimitives.WriteInt32LittleEndian(row[20..], 1);
            var quantization = _receivers[chart.Receiver].Quantization;
            for (var component = 0; component < 4; component++)
                for (var channel = 0; channel < 3; channel++)
                    BinaryPrimitives.WriteInt16LittleEndian(row[(24 + component * 6 + channel * 2)..],
                        quantization.Encode(component, channel, chart.Coarse.Component(component)[channel]));
            hash.AppendData(row[..48]); stream.Write(row[..48]);
        }
        foreach (var page in _pages) {
            cancellationToken.ThrowIfCancellationRequested();
            page.Id.Value.TryWriteBytes(row[..16], bigEndian: true, out _);
            _ = Convert.FromHexString(page.ContentHash, row.Slice(16, 32), out _, out _);
            BinaryPrimitives.WriteInt32LittleEndian(row[48..], page.Receiver);
            BinaryPrimitives.WriteInt32LittleEndian(row[52..], page.Level);
            BinaryPrimitives.WriteInt32LittleEndian(row[56..], page.X);
            BinaryPrimitives.WriteInt32LittleEndian(row[60..], page.Y);
            BinaryPrimitives.WriteInt32LittleEndian(row[64..], page.Edge);
            hash.AppendData(row[..68]); stream.Write(row[..68]);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> footer = stackalloc byte[32]; hash.GetHashAndReset(footer); stream.Write(footer);
    }

    public static PbrLightmapData Load(Stream stream, ulong maximumBytes = MaximumEncodedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes < 236) throw new InvalidDataException("Lightmap manifest header exceeds its decode budget.");
        try {
            Span<byte> header = stackalloc byte[204]; stream.ReadExactly(header);
            if (!header[..8].SequenceEqual("SIALMIDX"u8)) throw new InvalidDataException("Invalid lightmap manifest signature.");
            var receiverCount = BinaryPrimitives.ReadInt32LittleEndian(header[88..]);
            var pageCount = BinaryPrimitives.ReadInt32LittleEndian(header[92..]);
            var chartCount = BinaryPrimitives.ReadInt32LittleEndian(header[96..]); Admit(receiverCount, chartCount, pageCount, maximumBytes);
            var id = new AssetId(new Guid(header.Slice(8, 16), bigEndian: true));
            if (!id.IsValid) throw new InvalidDataException("Lightmap manifest needs a logical product identity.");
            var scene = Convert.ToHexString(header.Slice(24, 32)); var bake = Convert.ToHexString(header.Slice(56, 32));
            var settings = PbrLightmapBakeSettings.Decode(header[100..]);
            var bytes = (long)receiverCount * 120 + (long)chartCount * 48 + (long)pageCount * 68 + 32;
            if (stream.CanSeek && stream.Length - stream.Position != bytes)
                throw new InvalidDataException("Lightmap manifest length differs from its counts.");
            cancellationToken.ThrowIfCancellationRequested();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
            var receivers = new PbrLightmapReceiver[receiverCount]; var pages = new PbrLightmapPageDescriptor[pageCount];
            var charts = new PbrLightmapChart[chartCount];
            Span<byte> row = stackalloc byte[120]; Span<float3> scales = stackalloc float3[4];
            for (var at = 0; at < receiverCount; at++) {
                cancellationToken.ThrowIfCancellationRequested(); stream.ReadExactly(row); hash.AppendData(row);
                receivers[at]=PbrLightmapReceiver.Decode(row);
            }
            for (var at = 0; at < chartCount; at++) {
                cancellationToken.ThrowIfCancellationRequested(); stream.ReadExactly(row[..48]); hash.AppendData(row[..48]);
                var receiver = BinaryPrimitives.ReadInt32LittleEndian(row);
                if ((uint)receiver >= receiverCount || BinaryPrimitives.ReadInt32LittleEndian(row[20..]) != 1)
                    throw new InvalidDataException("Invalid intrinsic chart receiver or coverage.");
                var quantization = receivers[receiver].Quantization;
                for (var component = 0; component < 4; component++)
                    for (var channel = 0; channel < 3; channel++) {
                        var encoded = BinaryPrimitives.ReadInt16LittleEndian(row[(24 + component * 6 + channel * 2)..]);
                        if (encoded == short.MinValue || (encoded != 0 && quantization.Component(component)[channel] == 0))
                            throw new InvalidDataException("Invalid intrinsic chart coefficient encoding.");
                        scales[component][channel] = quantization.Decode(component, channel, encoded);
                    }
                charts[at] = new(receiver, BinaryPrimitives.ReadInt32LittleEndian(row[4..]), BinaryPrimitives.ReadInt32LittleEndian(row[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(row[12..]), BinaryPrimitives.ReadInt32LittleEndian(row[16..]),
                    new(scales[0], scales[1], scales[2], scales[3], true));
            }
            for (var at = 0; at < pageCount; at++) {
                cancellationToken.ThrowIfCancellationRequested(); stream.ReadExactly(row[..68]); hash.AppendData(row[..68]);
                pages[at] = new(new(new Guid(row[..16], bigEndian: true)), Convert.ToHexString(row.Slice(16, 32)),
                    BinaryPrimitives.ReadInt32LittleEndian(row[48..]), BinaryPrimitives.ReadInt32LittleEndian(row[52..]),
                    BinaryPrimitives.ReadInt32LittleEndian(row[56..]), BinaryPrimitives.ReadInt32LittleEndian(row[60..]),
                    BinaryPrimitives.ReadInt32LittleEndian(row[64..]));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Span<byte> expected = stackalloc byte[32]; Span<byte> actual = stackalloc byte[32];
            stream.ReadExactly(expected); hash.GetHashAndReset(actual);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("Lightmap manifest checksum mismatch.");
            if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing lightmap manifest data.");
            var data = new PbrLightmapData(id, scene, settings, receivers, charts, pages, cancellationToken);
            if (data.BakeIdentity != bake) throw new InvalidDataException("Lightmap bake identity differs from its reference inputs.");
            return data;
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated lightmap manifest.", error); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid lightmap manifest.", error); }
    }
}
