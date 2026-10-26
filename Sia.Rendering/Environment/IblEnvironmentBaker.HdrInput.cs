using System.Globalization;
using System.Text;

namespace Sia.Engine.Rendering;

public static partial class IblEnvironmentBaker
{
    // Deliberately bounded linear RGBE profile: modern scanline RLE, top-to-bottom -Y/+X.
    // Other color spaces, exposure/gamma transforms and historical encodings require explicit conversion.
    private static (int Width, int Height, byte[] Pixels) ReadHdr(Stream source, ulong maximumInputBytes,
        ulong maximumWorkingBytes, CancellationToken cancellationToken)
    {
        var input = new HdrInput(source, maximumInputBytes, cancellationToken);
        var signature = input.Line();
        if (signature is not ("#?RADIANCE" or "#?RGBE")) throw new InvalidDataException("Expected a Radiance RGBE header.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        while (true) {
            var line = input.Line();
            if (line.Length == 0) break;
            if (line[0] == '#') continue;
            var equals = line.IndexOf('=');
            if (equals <= 0 || !fields.Add(line[..equals])) throw new InvalidDataException("Invalid or duplicate HDR header field.");
            var value = line[(equals + 1)..];
            switch (line[..equals]) {
                case "FORMAT" when value == "32-bit_rle_rgbe": break;
                case "GAMMA" or "EXPOSURE" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == 1: break;
                case "PRIMARIES" when value.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 8 } primaries
                    && primaries.All(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p == 0): break;
                default: throw new InvalidDataException("HDR input requires linear RGBE without implicit header transforms.");
            }
        }
        if (!fields.Contains("FORMAT")) throw new InvalidDataException("Missing RGBE format.");
        var dimensions = input.Line().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (dimensions.Length != 4 || dimensions[0] != "-Y" || dimensions[2] != "+X"
            || !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) || height <= 0
            || !int.TryParse(dimensions[3], NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width is < 8 or > 32767)
            throw new InvalidDataException("HDR input requires modern RLE rows with -Y height +X width, width 8..32767.");
        if (HdrWorkingBytes(width, height) > maximumWorkingBytes) throw new InvalidDataException("HDR decode and projection exceed the working budget.");
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++) {
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Byte() != 2 || input.Byte() != 2 || input.Byte() != width >> 8 || input.Byte() != (width & 255))
                throw new InvalidDataException("Invalid HDR scanline marker or width.");
            for (var channel = 0; channel < 4; channel++) {
                var x = 0;
                while (x < width) {
                    var code = input.Byte();
                    var count = code > 128 ? code - 128 : code;
                    if (count == 0 || count > width - x) throw new InvalidDataException("Invalid HDR RLE span.");
                    if (code > 128) {
                        var value = (byte)input.Byte();
                        for (var end = x + count; x < end; x++) pixels[(y * width + x) * 4 + channel] = value;
                    }
                    else for (var end = x + count; x < end; x++) pixels[(y * width + x) * 4 + channel] = (byte)input.Byte();
                }
            }
        }
        if (input.ByteOrEnd() != -1) throw new InvalidDataException("Trailing HDR data.");
        return (width, height, pixels);
    }

    private sealed class HdrInput(Stream source, ulong maximumBytes, CancellationToken cancellationToken)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _at, _end, _headerBytes;
        private ulong _read;

        internal int ByteOrEnd()
        {
            if (_at == _end) {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = maximumBytes - _read;
                var count = (int)System.Math.Min((ulong)_buffer.Length, remaining == ulong.MaxValue ? remaining : remaining + 1);
                _end = source.Read(_buffer, 0, count); _at = 0; _read += (ulong)_end;
                if (_read > maximumBytes) throw new InvalidDataException("HDR encoded input exceeds its byte budget.");
                if (_end == 0) return -1;
            }
            return _buffer[_at++];
        }

        internal int Byte() => ByteOrEnd() is var value && value >= 0 ? value : throw new EndOfStreamException("Truncated HDR input.");

        internal string Line()
        {
            Span<byte> line = stackalloc byte[1024]; var length = 0;
            while (true) {
                var value = Byte();
                if (++_headerBytes > 8192) throw new InvalidDataException("HDR header exceeds 8 KiB.");
                if (value == '\n') break;
                if (value is < 32 or > 126 && value != '\r') throw new InvalidDataException("HDR header must be ASCII text.");
                if (length == line.Length) throw new InvalidDataException("HDR header line exceeds 1 KiB.");
                line[length++] = (byte)value;
            }
            if (length != 0 && line[length - 1] == '\r') length--;
            return Encoding.ASCII.GetString(line[..length]);
        }
    }
}
