using Sia;
using Sia.Asset;

namespace Sia.Engine.Rendering.Pbr;

[SiaAsset]
[SiaTemplate("PbrTextureAsset")]
public partial record PbrTextureRecord : AssetRecord, ILoadable<PbrTextureRecord>, ILoadable<PbrTextureRecord, ulong>
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
        if (!signature.SequenceEqual("SIATEXI1"u8)) throw new InvalidDataException("Invalid texture signature.");
        var mips = PbrTextureMipsData.Load(stream, maximumBytes, signature);
        return new() { Data = mips.Tail, Mips = mips, Name = name };
    }
}
