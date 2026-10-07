using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sia.Engine.Rendering;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class SceneTracePackingTests
{
    // Captured from the previous list-based builder before changing its scratch storage.
    [Theory]
    [InlineData(1, 0, 176, "0C20CA07F2D0A7F6D0C064C8B78057EFF94E4AB9487C7237076F4DC9F72D95AA")]
    [InlineData(1, 1, 176, "4CC83F64CDD560B4792CFB251157E221C77053DAB5E06DCC1FB12504327B0117")]
    [InlineData(1, 2, 176, "4EDA6009937F88B8D7AAD98930933F63C0E0576DB911D7B1233E146D97B96B0A")]
    [InlineData(8, 0, 736, "7F90848509F073977289647A0B9B8F0E959AF91A255235D158BC501AE20ED9D0")]
    [InlineData(8, 1, 736, "D9C24CC312DE1941BD0D740BA791E1558560C06A90326EEED98A0B769BEB9E89")]
    [InlineData(8, 2, 736, "C2C1E0ED0A298C2F3CBF91FE8DD569A4BA04F088559896C0151507A9CC4D1C9C")]
    [InlineData(9, 0, 912, "B7D20E8272E4F273DED7E297D88886428787FB04977C13DD2CA6E940F6D964DB")]
    [InlineData(9, 1, 912, "818E844C2CA91EEC4884FA6B8F975C9E6BEF5AE63316D4358EB682EE3FACBD2A")]
    [InlineData(9, 2, 912, "1F8222BBFF1481AEE6B1BF93D3241BFA7B89E045F34B14601FB32F38F8D7C4C5")]
    [InlineData(17, 0, 1632, "06EDC41FC940A3468221B6EC04E508A7E2A43E15321D18AF0BF8A06C38EF5322")]
    [InlineData(17, 1, 1632, "353CCC330D1339FB239A3486F701D2529BE31E095D7281630A9FCE9AB31377F0")]
    [InlineData(17, 2, 1632, "BA97A37BDC2C7FC39A78791BF4CF04ADF0BECBB5D7C096D5419F16C9A7849728")]
    [InlineData(97, 0, 5936, "DDF0F3FB45C4C9F4DA1973362835E95F64F61F3EADE4B4C8B0D2E9DADA54EF94")]
    [InlineData(97, 1, 5936, "434592CBB43043C94B6ED0C28876E92E96F0F2A49176F36BA3934AC875B0E20B")]
    [InlineData(97, 2, 5936, "94103BDA9343C653BD00C0F3C980C5B0F651740DB63A3886F03995DC24CC240B")]
    [InlineData(257, 0, 10656, "F8B98CC8E3956141379C05EC5C0FF8B4E1A79504A8EBE1A66CE305F4A1D33324")]
    [InlineData(257, 1, 10656, "B63E4DD90F1048862C91F151F6BC20CCED639CECD043957E140D9A6BA8132529")]
    [InlineData(257, 2, 10656, "1EEC5EB1FAC9F6C29ADBBD47D9602C598680DE996AF2E9D3CCE93A10DC81F28A")]
    [InlineData(513, 0, 17824, "87F14CC5D76042462319962709430DFF4446BA31AF796A8AC138B5EAD596E047")]
    [InlineData(513, 1, 17824, "9C068A0FBA4EBDC39E594B314315616A142C9DE16C17680781081D2F1A3D7C6F")]
    [InlineData(513, 2, 17824, "0DE5294959BEE6112831A629BB1C431FF1D525E8F55C5918C4B79CDB93E94CDA")]
    public void PackedHierarchyPreservesExistingBytes(int count, int axis, int bytes, string hash)
    {
        var triangles = CreateTriangles(count, axis);
        var trace = new SceneTraceData(triangles);
        var packed = trace.Packed.Span;
        Assert.Equal(bytes, packed.Length * 16);
        Assert.Equal(count, trace.TriangleCount);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(packed))));
        Assert.Equal(hash, Convert.ToHexString(trace.Identity.Span));
        var composed = SceneTraceData.Create(new ReversedTriangles(triangles.Reverse().ToArray()));
        Assert.Equal(hash, Convert.ToHexString(composed.Identity.Span));
        Assert.Equal(trace.Packed.ToArray(), composed.Packed.ToArray());
        var lo = new float3(float.PositiveInfinity);
        var hi = new float3(float.NegativeInfinity);
        foreach (var triangle in triangles) {
            lo = math.min(lo, math.min(triangle.A, math.min(triangle.B, triangle.C)));
            hi = math.max(hi, math.max(triangle.A, math.max(triangle.B, triangle.C)));
        }
        Assert.Equal(lo, trace.Bounds.Min);
        Assert.Equal(hi, trace.Bounds.Max);
        Assert.Equal(packed[0].x, packed[2].w); // Root escape covers all nodes.
        Assert.Equal(0, packed[3].w); // First triangle in the root's subtree.
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4_000_001)]
    public void InvalidSourceCountIsRejectedBeforeReading(int count)
        => Assert.Throws<ArgumentException>(() => SceneTraceData.Create(new UnreadableTriangles(count)));

    [Fact]
    public void InvalidIdentityIsRejectedBeforeReading()
        => Assert.Throws<ArgumentException>(() => SceneTraceData.Create(new UnreadableTriangles(1), sceneIdentity: new byte[31]));

    [Fact]
    public void ImpossibleBudgetIsRejectedBeforeReading()
        => Assert.Throws<ArgumentException>(() => SceneTraceData.Create(new UnreadableTriangles(1), maximumBytes: 1));

    [Fact]
    public void CompletedTraceOwnsPackedDataAndIdentity()
    {
        var triangles = CreateTriangles(17, 0);
        var identity = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var expectedIdentity = identity.ToArray();
        var expected = new SceneTraceData(triangles);
        var storage = triangles.Reverse().ToArray();
        var actual = SceneTraceData.Create(new ReversedTriangles(storage), sceneIdentity: identity);
        Array.Clear(storage);
        Array.Clear(identity);
        Assert.Equal(expected.Packed.ToArray(), actual.Packed.ToArray());
        Assert.Equal(expectedIdentity, actual.Identity.ToArray());
    }

    private readonly struct UnreadableTriangles(int count) : ISceneTraceTriangleSource
    {
        public int Count => count;
        public SceneTraceTriangle this[int index] => throw new InvalidOperationException("Source was read before validation.");
    }

    private readonly ref struct ReversedTriangles(ReadOnlySpan<SceneTraceTriangle> triangles) : ISceneTraceTriangleSource
    {
        private readonly ReadOnlySpan<SceneTraceTriangle> _triangles = triangles;
        public int Count => _triangles.Length;
        public SceneTraceTriangle this[int index] => _triangles[_triangles.Length - 1 - index];
    }

    private static SceneTraceTriangle[] CreateTriangles(int count, int axis)
    {
        float3 Rotate(float3 p) => axis switch {
            0 => p,
            1 => new(p.z, p.x, p.y),
            _ => new(p.y, p.z, p.x)
        };
        return Enumerable.Range(0, count).Select(i => {
            // Repeated grid cells exercise centroid ties, shared vertices and signed zero.
            var x = i % 16;
            var y = (i / 16) % 8;
            var z = i % 2 == 0 ? -0f : 0f;
            return new SceneTraceTriangle(
                Rotate(new(x, y, z)), Rotate(new(x + 1, y, 0f)), Rotate(new(x, y + 1, z)),
                new((i % 3 + 1) * .125f), new((i % 5) * .25f), i % 2 == 0);
        }).ToArray();
    }
}
