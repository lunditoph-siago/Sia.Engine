namespace Sia.Engine.Rendering.Pbr;

// CPU/GPU coverage contract. The WGSL hash uses the same unsigned operations;
// no frame counter, runtime Entity identity, cutoff or opacity rewrite enters it.
internal static class PbrShadowCoverage
{
    internal static bool IsCandidate(PbrMaterialAsset material) => material.AlphaBlend && material.Opacity > 0;

    internal static uint Hash(uint value)
    {
        unchecked {
            value ^= value >> 16; value *= 0x7feb352du;
            value ^= value >> 15; value *= 0x846ca68bu;
            return value ^ (value >> 16);
        }
    }

    internal static uint StableHash(uint x, uint y, uint light, uint instance, uint primitive)
    {
        var value = Hash(x ^ 0x9e3779b9u);
        value = Hash(value ^ y); value = Hash(value ^ light);
        value = Hash(value ^ instance); return Hash(value ^ primitive);
    }

    internal static float Threshold(uint hash) => (hash >> 8) * (1f / 16777216f);
    internal static bool Covers(float alpha, uint hash) => alpha > 0 && (alpha >= 1 || Threshold(hash) < alpha);
    internal static bool NeedsRefresh(ulong previousPose, ulong pose, ulong previousAlpha, ulong alpha)
        => previousPose != pose || previousAlpha != alpha;
}
