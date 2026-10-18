using System.Buffers;
using Sia;
using Sia.Math;
using Sia.WebGPU;

namespace Sia.Engine.Rendering.Pbr;

internal sealed partial class PbrGpuScene
{
    public PbrGpuScene(in GpuFrame frame,PbrRasterScene source,Entity groupLayout,ulong budget,int[] materialBatches)
    {
        _gpu=new(frame,budget);
        float4[]? vertices=null; uint4[]? attributes=null; uint[]? tangents=null; uint[]? indices=null;
        try {
            using var layout=new PbrRasterLayout(source,materialBatches,budget);
            _instanceMotion = new(source, layout);
            var vertexBytes=(ulong)layout.VertexCount*PbrRasterVertex.Bytes;
            var indexBytes=(ulong)layout.IndexCount*4;
            layout.ValidateBindingLimit(_gpu.Limits.MaxStorageBufferBindingSize);
            VertexCount=layout.VertexCount; VertexPlanes=PbrRasterVertex.Planes; TriangleCount=layout.TriangleCount;
            SingleSidedTriangles=layout.SingleSidedTriangles; OpaqueTriangles=layout.OpaqueTriangles;
            Bounds=layout.Bounds; Opaque=layout.Opaque; Transparent=layout.Transparent;
            Instances=_gpu.Upload<PbrInstanceGpu>(layout.Instances.Length==0?[default]:layout.Instances);
            Vertices=_gpu.Buffer(vertexBytes,WGPUBufferUsage.Storage|WGPUBufferUsage.Vertex|WGPUBufferUsage.CopySrc|WGPUBufferUsage.CopyDst);
            Topology=_gpu.Buffer(indexBytes,WGPUBufferUsage.Storage|WGPUBufferUsage.Index|WGPUBufferUsage.CopySrc|WGPUBufferUsage.CopyDst);
            const int vertexBatch=16384;
            vertices=ArrayPool<float4>.Shared.Rent(vertexBatch);
            attributes=ArrayPool<uint4>.Shared.Rent(vertexBatch);
            tangents=ArrayPool<uint>.Shared.Rent(vertexBatch*2);
            indices=ArrayPool<uint>.Shared.Rent(65536);
            for(var slot=0;slot<layout.Geometry.Length;slot++) {
                var geometry=layout.Geometry[slot];
                for(var first=0;first<geometry.VertexCount;first+=vertexBatch) {
                    var count=System.Math.Min(vertexBatch,geometry.VertexCount-first);
                    geometry.PackRasterVertices(vertices,attributes,tangents,first,count);
                    var offset=(ulong)layout.VertexOffsets[slot]+(uint)first;
                    Wgpu.WriteBuffer<float4>(_gpu.Queue,Vertices.GetWgpu<WGPUBuffer>(),offset*16,vertices.AsSpan(0,count));
                    Wgpu.WriteBuffer<uint4>(_gpu.Queue,Vertices.GetWgpu<WGPUBuffer>(),((ulong)VertexCount+offset)*16,attributes.AsSpan(0,count));
                    Wgpu.WriteBuffer<uint>(_gpu.Queue,Vertices.GetWgpu<WGPUBuffer>(),(ulong)VertexCount*32+offset*8,tangents.AsSpan(0,count*2));
                }
            }
            var writer=new PbrResidentGeometry.IndexWriter(indices,
                (values,first)=>Wgpu.WriteBuffer<uint>(_gpu.Queue,Topology.GetWgpu<WGPUBuffer>(),(ulong)first*4,values));
            for (var slot = 0; slot < layout.Geometry.Length; slot++)
                for (var lod = 0; lod < layout.Lods[slot].Length; lod++)
                    layout.Geometry[slot].WriteIndices(ref writer, layout.VertexOffsets[slot], lod);
            writer.Flush();
            Group=GpuBinding.Group(_gpu,groupLayout,[GpuBinding.Buffer(0,Vertices),GpuBinding.Buffer(1,Topology),GpuBinding.Buffer(2,Instances)]);
        }
        catch { _gpu.Dispose(); throw; }
        finally {
            if(vertices is not null) ArrayPool<float4>.Shared.Return(vertices);
            if(attributes is not null) ArrayPool<uint4>.Shared.Return(attributes);
            if(tangents is not null) ArrayPool<uint>.Shared.Return(tangents);
            if(indices is not null) ArrayPool<uint>.Shared.Return(indices);
        }
    }
}
