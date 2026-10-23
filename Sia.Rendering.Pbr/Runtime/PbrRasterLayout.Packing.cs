using Sia.Math;
using System.Runtime.InteropServices;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrRasterLayout
{
    private readonly bool _tablePacking;
    private readonly ulong _maximumBytes;
    private ulong _fixedBytes;
    private Dictionary<(uint X,uint Y,uint Z),uint>? _positionIndices;
    private List<uint>? _positions;
    private Dictionary<uint,uint>? _bakeIndices;
    private List<uint>? _bakeComponents;

    // 0: original 40-byte planes. 1: two inline 20-byte rows. 2: exact shared tables.
    internal uint VertexPackingMode { get; private set; }
    internal ulong VertexBufferBytes => VertexPackingMode switch {
        0 => (ulong)VertexCount*PbrRasterVertex.Bytes,
        1 => (ulong)VertexCount*20,
        _ => (ulong)VertexCount*12+(ulong)_positions!.Count*4
    };
    internal ulong AttributeBufferBytes => VertexPackingMode switch {
        0 => 0,
        1 => (ulong)VertexCount*20,
        _ => (ulong)VertexCount*16+(ulong)_bakeComponents!.Count*4
    };
    internal ReadOnlySpan<uint> Positions => _positions is null ? [] : CollectionsMarshal.AsSpan(_positions);
    internal ReadOnlySpan<uint> BakeComponents => _bakeComponents is null ? [] : CollectionsMarshal.AsSpan(_bakeComponents);
    private uint MinimumVertexBytes => _tablePacking && _bakeIndices is not null ? 28u : PbrRasterVertex.Bytes;

    private static (uint,uint,uint) PositionKey(float3 p) =>
        (BitConverter.SingleToUInt32Bits(p.x),BitConverter.SingleToUInt32Bits(p.y),BitConverter.SingleToUInt32Bits(p.z));

    private void RegisterPacking(PbrResidentGeometry geometry)
    {
        if(_bakeIndices is null) return;
        for(var at=0;at<geometry.VertexCount;at++) {
            geometry.RasterCoordinates(at,out var position,out var bake);
            var key=PositionKey(position);
            if(!_positionIndices!.ContainsKey(key)) {
                _positionIndices.Add(key,(uint)(_positions!.Count/3));
                _positions.Add(key.Item1); _positions.Add(key.Item2); _positions.Add(key.Item3);
            }
            if(!Bake(BitConverter.SingleToUInt32Bits(bake.x)) || !Bake(BitConverter.SingleToUInt32Bits(bake.y))) {
                ClearPackingTables(); return;
            }
        }
        bool Bake(uint bits) {
            if(_bakeIndices.ContainsKey(bits)) return true;
            if(_bakeIndices.Count==65536) return false;
            _bakeIndices.Add(bits,(uint)_bakeComponents!.Count); _bakeComponents.Add(bits); return true;
        }
    }

    private ulong CompactVertexBytes => checked((ulong)VertexCount*28+(ulong)_positions!.Count*4+(ulong)_bakeComponents!.Count*4);

    private void CheckPackingBudget(ulong fixedBytes)
    {
        var bytes=checked((ulong)VertexCount*PbrRasterVertex.Bytes);
        if(_bakeIndices is not null) bytes=System.Math.Min(bytes,CompactVertexBytes);
        if(bytes>_maximumBytes-fixedBytes) throw new ArgumentException("Raster vertices/tables exceed the remaining geometry budget.","maximumBytes");
    }

    private void SelectPacking(ulong fixedBytes)
    {
        _fixedBytes=fixedBytes;
        if(_tablePacking) {
            VertexPackingMode=1;
            if(_bakeIndices is not null && CompactVertexBytes<(ulong)VertexCount*PbrRasterVertex.Bytes) VertexPackingMode=2;
            else ClearPackingTables();
        }
        RequiredBytes=checked(fixedBytes+VertexBufferBytes+AttributeBufferBytes);
    }

    internal void PackRows(ReadOnlySpan<float4> positions,ReadOnlySpan<uint4> attributes,ReadOnlySpan<uint> tangents,
        Span<uint> positionRows,Span<uint> attributeRows)
    {
        var stride=VertexPackingMode==2 ? 3 : 5;
        var attributeStride=VertexPackingMode==2 ? 4 : 5;
        for(var at=0;at<positions.Length;at++) {
            var p=positions[at]; var a=attributes[at]; var i=at*stride; var j=at*attributeStride;
            if(VertexPackingMode==2) {
                positionRows[i]=_positionIndices![PositionKey(p.xyz)];
                positionRows[i+1]=BitConverter.SingleToUInt32Bits(p.w); positionRows[i+2]=a.x;
                attributeRows[j+3]=_bakeIndices![a.z]|(_bakeIndices[a.w]<<16);
            }
            else {
                var key=PositionKey(p.xyz);
                positionRows[i]=key.Item1; positionRows[i+1]=key.Item2; positionRows[i+2]=key.Item3;
                positionRows[i+3]=BitConverter.SingleToUInt32Bits(p.w); positionRows[i+4]=a.x;
                attributeRows[j+3]=a.z; attributeRows[j+4]=a.w;
            }
            attributeRows[j]=a.y; attributeRows[j+1]=tangents[at*2]; attributeRows[j+2]=tangents[at*2+1];
        }
    }

    private void ClearPackingTables() { _positionIndices=null; _positions=null; _bakeIndices=null; _bakeComponents=null; }
}
