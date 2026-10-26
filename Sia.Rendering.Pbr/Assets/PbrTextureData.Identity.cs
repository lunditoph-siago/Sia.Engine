namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrTextureData
{
    private string? _sourceLevel0Hash;
    private string? _level0Hash;

    // An indexed bootstrap carries its verified finest-mip descriptor. Whole
    // inputs can compute the same canonical single-level identity without IO.
    internal string SourceLevel0Hash => _sourceLevel0Hash ??= Level0Hash;

    internal string Level0Hash => _level0Hash ??= MipLevels.Length == 1 ? EncodedHash
        : new PbrTextureData(Width, Height, Srgb, Sampler, [MipLevels.Span[0]]).EncodedHash;

    internal PbrTextureData WithSourceLevel0Hash(string hash)
        => new(Width, Height, Srgb, Sampler, MipLevels.ToArray()) {
            _sourceLevel0Hash = hash, _encodedHash = _encodedHash, _level0Hash = _level0Hash
        };
}
