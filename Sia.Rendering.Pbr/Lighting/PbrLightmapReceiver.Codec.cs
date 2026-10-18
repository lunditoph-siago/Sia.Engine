using System.Buffers.Binary;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public readonly partial record struct PbrLightmapReceiver
{
    internal void Encode(Span<byte> bytes)
    {
        InstanceId.TryWriteBytes(bytes[..16],bigEndian:true,out _);
        _=Convert.FromHexString(SourceHash,bytes.Slice(16,32),out _,out _);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[48..],MaterialSlot);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[52..],Resolution);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[56..],Padding);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[60..],FirstChart);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[64..],ChartCount);
        for (var component=0;component<4;component++) for (var channel=0;channel<3;channel++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(68+component*12+channel*4)..],Quantization.Component(component)[channel]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[116..],FirstMip);
    }

    internal static PbrLightmapReceiver Decode(ReadOnlySpan<byte> bytes)
    {
        Span<float3> scales=stackalloc float3[4];
        for (var component=0;component<4;component++) for (var channel=0;channel<3;channel++)
            scales[component][channel]=BinaryPrimitives.ReadSingleLittleEndian(bytes[(68+component*12+channel*4)..]);
        var quantization=new PbrLightmapQuantization(scales[0],scales[1],scales[2],scales[3]); quantization.Validate();
        return new(new Guid(bytes[..16],bigEndian:true),BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]),
            Convert.ToHexString(bytes.Slice(16,32)),BinaryPrimitives.ReadInt32LittleEndian(bytes[52..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[56..]),BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[64..]),quantization,BinaryPrimitives.ReadInt32LittleEndian(bytes[116..]));
    }
}
