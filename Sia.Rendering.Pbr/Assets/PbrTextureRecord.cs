using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

[SiaAsset]
[SiaTemplate("PbrTextureAsset")]
public partial record PbrTextureRecord : AssetRecord, ILoadable<PbrTextureRecord>, ILoadable<PbrTextureRecord, ulong>,
    IAsyncLoadable<PbrTextureRecord, ulong>
{
    public required PbrTextureData Data { get; init; }
    /// <summary>Optional full-resolution mip index; Data is its independently validated bootstrap tail.</summary>
    public PbrTextureMipsData? Mips { get; init; }
    public static PbrTextureRecord Load(Stream stream, string? name = null) => Load(stream, 128ul * 1024 * 1024, name);
    public static PbrTextureRecord Load(Stream stream, ulong maximumBytes, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes < 64) throw new InvalidDataException("Texture header exceeds the decode limit.");
        Span<byte> signature = stackalloc byte[8];
        try { stream.ReadExactly(signature); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated texture file.", error); }
        if (signature.SequenceEqual("SIATEXR1"u8)) return new() { Data = PbrTextureData.Load(stream, maximumBytes, signature), Name = name };
        if (!signature.SequenceEqual("SIATEXI1"u8) && !signature.SequenceEqual("SIATEXI2"u8)) throw new InvalidDataException("Invalid texture signature.");
        var mips = PbrTextureMipsData.Load(stream, maximumBytes, signature);
        return new() { Data = mips.Tail, Mips = mips, Name = name };
    }

    public static async ValueTask<PbrTextureRecord> LoadAsync(Stream stream, ulong maximumBytes, string? name = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes < 64) throw new InvalidDataException("Texture header exceeds the decode limit.");
        try {
            var header = new byte[64];
            await stream.ReadExactlyAsync(header.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
            if (header.AsSpan(0, 8).SequenceEqual("SIATEXR1"u8))
                return new() { Data = await PbrTextureData.LoadAsync(stream, maximumBytes, header.AsMemory(0, 8), cancellationToken).ConfigureAwait(false), Name = name };
            var legacy = header.AsSpan(0, 8).SequenceEqual("SIATEXI1"u8);
            if (!legacy && !header.AsSpan(0, 8).SequenceEqual("SIATEXI2"u8)) throw new InvalidDataException("Invalid texture signature.");
            await stream.ReadExactlyAsync(header.AsMemory(8), cancellationToken).ConfigureAwait(false);
            var length = PbrTextureMipsData.ValidateHeader(header, maximumBytes);
            if (length > int.MaxValue)
                throw new InvalidDataException("Texture index exceeds its encoded preparation limit.");
            var bytes = new byte[(int)length]; header.CopyTo(bytes, 0);
            await stream.ReadExactlyAsync(bytes.AsMemory(64), cancellationToken).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Trailing texture index data.");
            using var input = new MemoryStream(bytes, writable: false);
            cancellationToken.ThrowIfCancellationRequested();
            return Load(input, maximumBytes, name);
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated texture file.", error); }
    }
}
