using System.Runtime.CompilerServices;
using Sia.Engine.Mesh;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

// Owns compact section remaps until upload completes. Source products remain borrowed from the caller.
internal sealed class PbrRasterLayout : IDisposable
{
    internal PbrResidentGeometry[] Geometry { get; private set; } = [];
    internal uint[] VertexOffsets { get; private set; } = [];
    internal int[] SourceGeometry { get; private set; } = [];
    internal PbrInstanceGpu[] Instances { get; private set; } = [];
    internal PbrGpuScene.Draw[] Opaque { get; private set; } = [];
    internal PbrGpuScene.Draw[] Transparent { get; private set; } = [];
    internal uint VertexCount { get; private set; }
    internal uint TriangleCount { get; private set; }
    internal uint IndexCount { get; private set; }
    internal MeshRasterLod[][] Lods { get; private set; } = [];
    internal uint SingleSidedTriangles { get; private set; }
    internal uint OpaqueTriangles { get; private set; }
    internal ulong RequiredBytes { get; private set; }
    internal Aabb Bounds { get; }
    private bool _disposed;

    internal PbrRasterLayout(PbrRasterScene source,int[] materialBatches,ulong maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(materialBatches);
        if(materialBatches.Length!=source.Materials.Length || materialBatches.Any(batch=>batch<0))
            throw new ArgumentException("Raster material batches must cover the acquired material table.",nameof(materialBatches));
        var instanceBytes=checked((ulong)System.Math.Max(1,source.Instances.Length)*(ulong)Unsafe.SizeOf<PbrInstanceGpu>());
        if(instanceBytes>maximumBytes) throw new ArgumentException("Raster instances exceed the geometry budget.",nameof(maximumBytes));
        Bounds=source.Bounds;
        var geometry=new List<PbrResidentGeometry>(); var offsets=new List<uint>(); var levels=new List<MeshRasterLod[]>();
        var shapes=new Dictionary<(MeshRasterData,int),int>(); var rows=new int[source.Geometry.Length]; Array.Fill(rows,-1);
        try {
            foreach(var instance in source.Instances.Span) {
                if(rows[instance.Geometry]>=0) continue;
                var value=source.Geometry.Span[instance.Geometry]; var key=(value.Product,value.Section);
                if(!shapes.TryGetValue(key,out var slot)) {
                    var end=IndexCount;
                    foreach(var level in value.Lods) end=checked(end+(uint)level.IndexCount);
                    var fixedBytes=checked(instanceBytes+(ulong)end*4+(ulong)VertexCount*PbrRasterVertex.Bytes);
                    if(fixedBytes>maximumBytes) throw new ArgumentException("Raster topology exceeds the remaining geometry budget.",nameof(maximumBytes));
                    var maximumVertices=(int)System.Math.Min((ulong)int.MaxValue,(maximumBytes-fixedBytes)/PbrRasterVertex.Bytes);
                    var prepared=new PbrResidentGeometry(value,maximumVertices); geometry.Add(prepared); slot=geometry.Count-1;
                    shapes.Add(key,slot); offsets.Add(VertexCount);
                    VertexCount=checked(VertexCount+(uint)prepared.VertexCount);
                    var lods = new MeshRasterLod[value.Lods.Length];
                    for (var lod = 0; lod < lods.Length; lod++) {
                        var input = value.Lods[lod];
                        lods[lod] = new(checked((int)IndexCount), input.IndexCount, input.Error);
                        IndexCount = checked(IndexCount + (uint)input.IndexCount);
                    }
                    levels.Add(lods);
                }
                rows[instance.Geometry]=slot;
            }
            Geometry=geometry.ToArray(); VertexOffsets=offsets.ToArray(); Lods=levels.ToArray();
            int Bin(int index) {
                var material=source.Materials.Span[source.Instances.Span[index].Material];
                return material.AlphaBlend?2:material.DoubleSided?1:0;
            }
            var order=Enumerable.Range(0,source.Instances.Length).OrderBy(Bin)
                .ThenBy(index=>materialBatches[source.Instances.Span[index].Material]).ToArray();
            Instances=new PbrInstanceGpu[order.Length]; SourceGeometry=new int[order.Length];
            var opaque=new List<PbrGpuScene.Draw>(); var transparent=new List<PbrGpuScene.Draw>();
            for(var row=0;row<order.Length;row++) {
                var original=order[row]; var instance=source.Instances.Span[original];
                var material=source.Materials.Span[instance.Material]; var slot=rows[instance.Geometry]; var prepared=Geometry[slot];
                var count=prepared.TriangleCount; var first=TriangleCount;
                var logicalCount = checked((uint)levels[slot].Sum(level => (long)level.IndexCount / 3));
                SourceGeometry[row]=slot;
                // z/w describe an ordered, disjoint logical triangle interval; y retains authored input order.
                Instances[row]=new(instance.Transform,math.transpose(math.inverse(instance.Transform)),
                    new((uint)instance.Material,(uint)original,first,logicalCount)) {
                    Geometry = new((uint)Lods[slot][0].FirstIndex / 3, 0, 0, 0) };
                var bounds=BoundsTransform.Apply(prepared.Bounds,instance.Transform);
                if(count!=0) (material.AlphaBlend?transparent:opaque).Add(new(first,prepared.TriangleCount,instance.Material,bounds,
                    material.DoubleSided,(uint)Lods[slot][0].FirstIndex,(uint)row) { SourceInstance=(uint)original, Lods=Lods[slot], ErrorScale=PbrGpuScene.TransformNorm(instance.Transform) });
                TriangleCount=checked(TriangleCount+logicalCount);
                if(Bin(original)==0) SingleSidedTriangles=TriangleCount;
                if(Bin(original)<=1) OpaqueTriangles=TriangleCount;
            }
            RequiredBytes=checked((ulong)VertexCount*PbrRasterVertex.Bytes+(ulong)IndexCount*4+instanceBytes);
            if(RequiredBytes>maximumBytes) throw new ArgumentException(
                $"Shared Raster geometry/instances require {RequiredBytes} bytes, exceeding budget {maximumBytes}.",nameof(maximumBytes));
            Opaque=opaque.ToArray(); Transparent=transparent.ToArray();
        }
        catch { foreach(var prepared in geometry) prepared.Dispose(); Geometry=[]; throw; }
    }

    internal int InstanceForTriangle(uint triangle)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(triangle>=TriangleCount) throw new ArgumentOutOfRangeException(nameof(triangle));
        var first=0; var end=Instances.Length;
        while(first<end) {
            var middle=first+(end-first)/2; var row=Instances[middle].Material;
            if(triangle<row.z) end=middle;
            else if(triangle-row.z>=row.w) first=middle+1;
            else return middle;
        }
        throw new InvalidOperationException("Raster logical triangle is not owned by an instance.");
    }

    internal void ValidateBindingLimit(ulong maximumBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if((ulong)VertexCount*PbrRasterVertex.Bytes>maximumBytes || (ulong)IndexCount*4>maximumBytes
            || (ulong)System.Math.Max(1,Instances.Length)*(ulong)Unsafe.SizeOf<PbrInstanceGpu>()>maximumBytes)
            throw new NotSupportedException("Shared Raster vertex, topology or instance buffer exceeds device binding limits.");
    }

    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        foreach(var geometry in Geometry) geometry.Dispose(); Geometry=[];
    }
}
