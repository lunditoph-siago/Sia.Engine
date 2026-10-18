using Sia.Graphics.Text;

namespace Sia.UI;

/// <summary>Owned font bytes and their existing CPU parser value; no atlas or GPU ownership.</summary>
public sealed class UiFontData
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    private readonly byte[] _bytes;
    public ReadOnlySpan<byte> Bytes => _bytes;
    public Font Font { get; }

    private UiFontData(byte[] bytes)
    {
        _bytes = bytes;
        try { Font = new Font(bytes); }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or KeyNotFoundException or OverflowException) {
            throw new InvalidDataException("Invalid font data.", error);
        }
    }

    public static UiFontData Create(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Font exceeds 16 MiB.");
        return new(bytes.ToArray());
    }

    public static UiFontData Load(Stream stream, int maximumBytes = MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes is < 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (stream.CanSeek && (stream.Length - stream.Position < 0 || stream.Length - stream.Position > maximumBytes))
            throw new InvalidDataException("Encoded font exceeds its byte limit.");
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        while (true) {
            var count = stream.Read(buffer.AsSpan(0, (int)System.Math.Min(buffer.Length, (long)maximumBytes - bytes.Length + 1)));
            if (count == 0) break;
            if (bytes.Length + count > maximumBytes) throw new InvalidDataException("Encoded font exceeds its byte limit.");
            bytes.Write(buffer.AsSpan(0, count));
        }
        return new(bytes.ToArray());
    }

    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(_bytes);
    }
}
