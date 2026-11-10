using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Math;

namespace Sia.Engine.Rendering;

public sealed partial class DisplayTransformData
{
    /// <summary>Writes versioned static data and a SHA-256 checksum; returns the complete encoded content identity.</summary>
    public string Write(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, s_utf8, leaveOpen: true)) {
            void Text(string value) { var bytes = s_utf8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
            void Vector(float3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
            Text(InputColorSpace); Text(OutputColorSpace); writer.Write(Convert.FromHexString(SourceRecipeHash));
            writer.Write((uint)Stages.Count);
            foreach (var stage in Stages) {
                writer.Write((byte)stage.Kind); writer.Write(stage.Inverse ? (byte)1 : (byte)0);
                writer.Write((byte)stage.NegativeStyle); writer.Write((byte)0);
                switch (stage.Kind) {
                    case DisplayTransformStage.Operation.Matrix:
                        Vector(stage.A); Vector(stage.B); Vector(stage.C); Vector(stage.D); break;
                    case DisplayTransformStage.Operation.Log: writer.Write(stage.Base); break;
                    case DisplayTransformStage.Operation.Exponent: Vector(stage.A); break;
                    case DisplayTransformStage.Operation.MonCurve: Vector(stage.A); Vector(stage.B); break;
                    case DisplayTransformStage.Operation.Lut3D:
                        writer.Write((uint)stage.Table!.Size);
                        foreach (var value in stage.Table.Rgb32F) writer.Write(value);
                        break;
                    default: throw new InvalidOperationException("Unknown static display stage.");
                }
            }
        }
        var payload = body.GetBuffer().AsSpan(0, checked((int)body.Length));
        Span<byte> header = stackalloc byte[16];
        "SIADSPT\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], checked((uint)payload.Length));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header); hash.AppendData(payload); var checksum = hash.GetHashAndReset();
        destination.Write(header); destination.Write(payload); destination.Write(checksum);
        hash.AppendData(header); hash.AppendData(payload); hash.AppendData(checksum);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static DisplayTransformData Read(Stream source, ulong maximumBytes = MaximumEncodedBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumBytes == 0 || maximumBytes > MaximumEncodedBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        try {
            Span<byte> header = stackalloc byte[16]; source.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            if (!header[..8].SequenceEqual("SIADSPT\0"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 1
                || length < 40 || (ulong)length + 48 > maximumBytes)
                throw new InvalidDataException("Invalid display transform signature, version or byte admission.");
            var payload = new byte[checked((int)length)]; source.ReadExactly(payload);
            Span<byte> checksum = stackalloc byte[32]; source.ReadExactly(checksum);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(header); hash.AppendData(payload);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), checksum) || source.ReadByte() != -1)
                throw new InvalidDataException("Display transform checksum mismatch or trailing data.");
            using var reader = new BinaryReader(new MemoryStream(payload, writable: false), s_utf8);
            string Text()
            {
                var size = reader.ReadUInt16();
                if (size == 0 || size > 256) throw new InvalidDataException("Invalid display color-space identity length.");
                var bytes = reader.ReadBytes(size);
                if (bytes.Length != size) throw new EndOfStreamException();
                return s_utf8.GetString(bytes);
            }
            float3 Vector() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var input = Text(); var output = Text(); var recipeBytes = reader.ReadBytes(32);
            if (recipeBytes.Length != 32) throw new EndOfStreamException();
            var count = reader.ReadUInt32();
            if (count == 0 || count > MaximumStages) throw new InvalidDataException("Invalid display stage count.");
            var stages = new DisplayTransformStage[(int)count]; var lookupCount = 0;
            for (var at = 0; at < stages.Length; at++) {
                var kind = (DisplayTransformStage.Operation)reader.ReadByte(); var inverse = reader.ReadByte();
                var style = (DisplayTransformNegativeStyle)reader.ReadByte(); var reserved = reader.ReadByte();
                if (inverse > 1 || !Enum.IsDefined(style) || reserved != 0) throw new InvalidDataException("Invalid display stage flags.");
                if ((kind is DisplayTransformStage.Operation.Matrix or DisplayTransformStage.Operation.Lut3D) && (inverse != 0 || style != DisplayTransformNegativeStyle.Clamp)
                    || kind == DisplayTransformStage.Operation.Log && style != DisplayTransformNegativeStyle.Clamp)
                    throw new InvalidDataException("Display stage has inapplicable flags.");
                switch (kind) {
                    case DisplayTransformStage.Operation.Matrix: stages[at] = DisplayTransformStage.Matrix(Vector(), Vector(), Vector(), Vector()); break;
                    case DisplayTransformStage.Operation.Log: stages[at] = DisplayTransformStage.Log(reader.ReadSingle(), inverse != 0); break;
                    case DisplayTransformStage.Operation.Exponent: stages[at] = DisplayTransformStage.Exponent(Vector(), style, inverse != 0); break;
                    case DisplayTransformStage.Operation.MonCurve: stages[at] = DisplayTransformStage.MonCurve(Vector(), Vector(), inverse != 0, style); break;
                    case DisplayTransformStage.Operation.Lut3D:
                        if (++lookupCount > MaximumLookupTables) throw new InvalidDataException("Display lookup count exceeds its admission.");
                        var size = reader.ReadUInt32();
                        if (size < 2 || size > MaximumLutSize) throw new InvalidDataException("Invalid display LUT size.");
                        var values = checked((int)(size * size * size * 3));
                        if ((ulong)values * 4 > (ulong)(reader.BaseStream.Length - reader.BaseStream.Position))
                            throw new InvalidDataException("Truncated display LUT payload.");
                        var rgb = new float[values];
                        for (var i = 0; i < rgb.Length; i++) rgb[i] = reader.ReadSingle();
                        stages[at] = DisplayTransformStage.Lut3D((int)size, rgb); break;
                    default: throw new InvalidDataException("Unknown display transform stage.");
                }
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing display stage data.");
            return Create(input, output, Convert.ToHexString(recipeBytes), stages, maximumBytes);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated display transform.", error); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid display transform data.", error); }
        catch (OverflowException error) { throw new InvalidDataException("Display transform length overflow.", error); }
    }
}
