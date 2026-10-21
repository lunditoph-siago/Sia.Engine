using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

internal static class PbrRasterVertex
{
    internal const int Planes=3;
    internal const int Bytes=40;

    internal static uint PackNormal(float3 direction) => math.cmax(math.abs(direction)) == 0 ? 0x80008000u : PackDirection(direction);

    private static uint PackDirection(float3 direction)
    {
        var scale=math.cmax(math.abs(direction));
        if (!float.IsFinite(scale)) throw new ArgumentException("Raster direction must be finite.", nameof(direction));
        var oct=float2.zero;
        if(scale>0) {
            direction/=scale; direction/=math.csum(math.abs(direction)); oct=direction.xy;
            if(direction.z<0) oct=new((1-MathF.Abs(oct.y))*(oct.x<0?-1:1),(1-MathF.Abs(oct.x))*(oct.y<0?-1:1));
        }
        var x=(short)System.Math.Clamp((int)MathF.Round(oct.x*32767),-32767,32767);
        var y=(short)System.Math.Clamp((int)MathF.Round(oct.y*32767),-32767,32767);
        return (uint)(ushort)x|((uint)(ushort)y<<16);
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
