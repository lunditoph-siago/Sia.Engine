using System.Text;

namespace Sia.Asset;

/// <summary>Bounded strict UTF-8 decoding for concrete text asset codecs.</summary>
public static class Utf8Text
{
    private static readonly UTF8Encoding s_Encoding = new(false, true);

    /// <summary>Reads a borrowed stream, stripping one initial UTF-8 BOM. Empty text is allowed.</summary>
    public static string Read(Stream stream, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (stream.CanSeek && (stream.Length - stream.Position < 0 || stream.Length - stream.Position > maximumBytes))
            throw new InvalidDataException("Encoded text exceeds its byte limit.");
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true) {
            var count = stream.Read(buffer.AsSpan(0, (int)System.Math.Min(buffer.Length, (long)maximumBytes - bytes.Length + 1)));
            if (count == 0) break;
            if (bytes.Length + count > maximumBytes) throw new InvalidDataException("Encoded text exceeds its byte limit.");
            bytes.Write(buffer.AsSpan(0, count));
        }
        var encoded = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
        if (encoded.StartsWith("\uFEFF"u8)) encoded = encoded[3..];
        try { return s_Encoding.GetString(encoded); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid UTF-8 text.", error); }
    }

    /// <summary>Validates Unicode and encoded size; concrete codecs decide text semantics.</summary>
    public static void Validate(string text, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (s_Encoding.GetByteCount(text) > maximumBytes)
            throw new ArgumentException("Text exceeds its UTF-8 byte limit.", nameof(text));
    }
}
