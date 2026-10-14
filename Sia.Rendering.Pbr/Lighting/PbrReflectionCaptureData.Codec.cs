using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrReflectionCaptureData
{
    /// <summary>Writes only bounded region/dependency metadata and returns the full encoded content hash. Leaves the stream open.</summary>
    public string Write(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination); cancellationToken.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[HeaderBytes]; "SIARFCAP"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], _staticInstances.Length);
        ProductId.Value.TryWriteBytes(header.Slice(12, 16), bigEndian: true, out _);
        Scene.Value.Value.TryWriteBytes(header.Slice(28, 16), bigEndian: true, out _);
        Convert.FromHexString(SceneIdentity).CopyTo(header[44..]);
        SourceEnvironment.Id.Value.TryWriteBytes(header.Slice(76, 16), bigEndian: true, out _);
        Convert.FromHexString(SourceEnvironment.ContentHash).CopyTo(header[92..]);
        Environment.Id.Value.TryWriteBytes(header.Slice(124, 16), bigEndian: true, out _);
        Convert.FromHexString(Environment.ContentHash).CopyTo(header[140..]);
        Vector(header[172..], Position); Vector(header[184..], Bounds.Min); Vector(header[196..], Bounds.Max);
        Settings.Encode(header[208..]); Convert.FromHexString(InputIdentity).CopyTo(header[244..]);
        destination.Write(header); hash.AppendData(header);
        Span<byte> id = stackalloc byte[16];
        foreach (var instance in _staticInstances) {
            cancellationToken.ThrowIfCancellationRequested(); instance.TryWriteBytes(id, bigEndian: true, out _);
            destination.Write(id); hash.AppendData(id);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> footer = stackalloc byte[32]; hash.GetCurrentHash(footer); destination.Write(footer); hash.AppendData(footer);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static PbrReflectionCaptureData Load(Stream source, ulong maximumBytes = 1024ul * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); cancellationToken.ThrowIfCancellationRequested();
        Span<byte> prefix = stackalloc byte[12]; source.ReadExactly(prefix);
        if (!prefix[..8].SequenceEqual("SIARFCAP"u8)) throw new InvalidDataException("Invalid reflection region metadata.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(prefix[8..]); Admit(count, maximumBytes);
        // Checks encoded validation scratch against the same independent ceiling before the remaining read.
        var length = HeaderBytes + count * 16;
        if ((ulong)length + 32 > maximumBytes) throw new ArgumentException("Capture encoded metadata exceeds its admission.");
        var bytes = new byte[length]; prefix.CopyTo(bytes); source.ReadExactly(bytes.AsSpan(12));
        Span<byte> footer = stackalloc byte[32]; source.ReadExactly(footer);
        if (!CryptographicOperations.FixedTimeEquals(footer, SHA256.HashData(bytes)) || source.ReadByte() != -1)
            throw new InvalidDataException("Reflection metadata checksum mismatch or trailing data.");
        cancellationToken.ThrowIfCancellationRequested();
        var instances = new Guid[count];
        for (var at = 0; at < count; at++) {
            cancellationToken.ThrowIfCancellationRequested(); instances[at] = new Guid(bytes.AsSpan(HeaderBytes + at * 16, 16), bigEndian: true);
        }
        AssetId Id(int at) => new(new Guid(bytes.AsSpan(at, 16), bigEndian: true));
        string HashAt(int at) => Convert.ToHexString(bytes.AsSpan(at, 32));
        var value = new PbrReflectionCaptureData(Id(12), new(Id(28)), HashAt(44), new(Id(76), HashAt(92)), new(Id(124), HashAt(140)),
            Vector(bytes.AsSpan(172)), new(Vector(bytes.AsSpan(184)), Vector(bytes.AsSpan(196))),
            PbrReflectionCaptureSettings.Decode(bytes.AsSpan(208, 36)), instances, cancellationToken);
        if (value.InputIdentity != HashAt(244)) throw new InvalidDataException("Reflection capture frozen inputs disagree with their identity.");
        return value;
    }
}
