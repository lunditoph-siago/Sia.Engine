namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapPageData
{
    public const int PackedTexelWords = 8;

    /// <summary>
    /// Copies two uint4 texels per lightmap sample into caller-owned upload scratch, without decoding/requantizing.
    /// Words0..5 pack low/high signed16 pairs in constant/Y/Z/X RGB order; word6 is coverage and word7 is zero.
    /// Receiver scales stay in independent metadata. Performs no GPU, IO or lifetime operation.
    /// </summary>
    public void CopyPackedCoefficients(Span<uint> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length != checked(TexelCount * PackedTexelWords))
            throw new ArgumentException("Packed Lightmap upload requires exactly eight words per texel.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        // A page is at most4096 texels. Check cancellation before this bounded copy;
        // no mid-copy cancellation exposes an unexpectedly partial output to callers.
        for (var at = 0; at < TexelCount; at++) {
            var output = destination.Slice(at * PackedTexelWords, PackedTexelWords);
            var coefficients = _coefficients.AsSpan(at * 12, 12);
            for (var pair = 0; pair < 6; pair++)
                output[pair] = (uint)(ushort)coefficients[pair * 2] | ((uint)(ushort)coefficients[pair * 2 + 1] << 16);
            output[6] = _coverage[at]; output[7] = 0;
        }
    }
}
