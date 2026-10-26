using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

internal static class PbrRasterVertex
{
    internal const int Planes=3;
    internal const int Bytes=40;

    internal static uint PackNormal(float3 direction) => math.cmax(math.abs(direction)) == 0 ? 0x80008000u : PackDirection(direction);

    private static uint PackDirection(float3 direction)
    {
        return StreamVertexEncoding.PackDirection(direction);
    }

    internal static uint4 Pack(float4 tangent,float2 bake,bool present)
    {
        var direction=tangent.xyz;
        var scale=math.cmax(math.abs(direction));
        var magnitude=0f;
        if(scale>0) { direction/=scale; magnitude=math.length(direction)*scale; }
        if(!float.IsFinite(magnitude)) throw new ArgumentException("Raster tangent magnitude is not representable.",nameof(tangent));
        var words=PackDirection(direction);
        var flags=(BitConverter.SingleToUInt32Bits(magnitude)&0xfffffffcu)|(tangent.w<0?1u:0u)|(present?2u:0u);
        return new(words,flags,BitConverter.SingleToUInt32Bits(bake.x),BitConverter.SingleToUInt32Bits(bake.y));
    }
}
