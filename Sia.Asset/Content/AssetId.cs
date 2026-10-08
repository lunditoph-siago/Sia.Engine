using System.Buffers.Binary;

namespace Sia.Asset;

/// <summary>Content identity, independent of a world, path or runtime instance.</summary>
public readonly record struct AssetId
{
    public const int ByteCount = 32;
    private readonly ulong _a, _b, _c, _d;

    public AssetId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount) throw new ArgumentException("An asset ID is 32 bytes.", nameof(bytes));
        _a = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        _b = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        _c = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        _d = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    }

    public void WriteBytes(Span<byte> destination)
    {
        if (destination.Length < ByteCount) throw new ArgumentException("An asset ID needs 32 bytes.", nameof(destination));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, _a);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], _c);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], _d);
    }

    public static AssetId Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length != ByteCount * 2) throw new FormatException("An asset ID has 64 hexadecimal characters.");
        return new(Convert.FromHexString(text));
    }

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[ByteCount];
        WriteBytes(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
