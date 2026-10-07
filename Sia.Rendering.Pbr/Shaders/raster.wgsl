#define_import_path pbr/raster

#import pbr/bindings
#import pbr/stream_selection_types

@group(2) @binding(12) var<storage, read> stream_work: array<vec4<u32>>;
@group(2) @binding(13) var<uniform> stream_selection: StreamSelection;

struct RasterVertex {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat, either) ordinal: u32
}

@vertex
fn raster_vertex(@builtin(vertex_index) index: u32) -> RasterVertex {
    let ordinal = index / 3u;
    return RasterVertex(frame.vp * world_corner(ordinal, index % 3u), ordinal);
}

@vertex
fn conventional_vertex(@builtin(vertex_index) index: u32) -> RasterVertex {
    let ordinal = index / 3u;
    return RasterVertex(frame.vp * world_corner(ordinal, index % 3u), ordinal | CONVENTIONAL_TRIANGLE_BIT);
}

@fragment
fn raster_fragment(input: RasterVertex) -> @location(0) u32 {
    return input.ordinal + 1u;
}

struct DepthRasterOutput {
    @location(0) id: u32,
    @builtin(frag_depth) depth: f32,
}

@fragment
fn raster_fragment_depth(input: RasterVertex) -> DepthRasterOutput {
    return DepthRasterOutput(input.ordinal + 1u, input.position.z);
}

@fragment
fn shadow_fragment_depth(@builtin(position) position: vec4<f32>) -> @builtin(frag_depth) f32 {
    return position.z;
}

#if STREAM_INSTANCES
fn stream_work_address(vertex: u32, instance: u32) -> vec2<u32> {
    let triangle = vertex / 3u;
    return vec2<u32>(triangle / STREAM_WORK_BLOCK_TRIANGLES + instance,
        triangle % STREAM_WORK_BLOCK_TRIANGLES);
}
#endif

@vertex
fn stream_vertex(
    @builtin(vertex_index) index: u32,
    @builtin(instance_index) instance_index: u32
) -> RasterVertex {
#if STREAM_INSTANCES
    let address = stream_work_address(index, instance_index);
    let record = address.x;
    let local = address.y;
    let work = stream_work[record];
    if (local >= work.z) {
        return RasterVertex(vec4<f32>(0.0, 0.0, 0.0, 1.0), 0u);
    }
    let ordinal = record * STREAM_WORK_BLOCK_TRIANGLES + local;
    let position = instances[work.y].transform * local_corner(work.x + local, index % 3u);
    return RasterVertex(frame.vp * position, ordinal);
#else
    return RasterVertex(vec4<f32>(0.0), 0u);
#endif
}

@vertex
fn stream_shadow_vertex(
    @builtin(vertex_index) index: u32,
    @builtin(instance_index) instance_index: u32
) -> @builtin(position) vec4<f32> {
#if STREAM_INSTANCES
    let address = stream_work_address(index, instance_index);
    let record = address.x;
    let local = address.y;
    let work = stream_work[record];
    if (local >= work.z) {
        return vec4<f32>(0.0, 0.0, 0.0, 1.0);
    }
    let position = instances[work.y].transform * local_corner(work.x + local, index % 3u);
    return shadow_matrix(stream_selection.output.z) * position;
#else
    return vec4<f32>(0.0);
#endif
}

@vertex
fn shadow_vertex(
#if COMPACT_VERTICES
    @location(0) vertex: vec4<u32>,
#else
    @location(0) p: vec4<f32>,
#endif
    @builtin(instance_index) packed: u32
) -> @builtin(position) vec4<f32> {
#if COMPACT_VERTICES
    let p = bitcast<vec4<f32>>(vertex);
#endif
    var position = vec4<f32>(p.xyz, 1.0);
#if LOCAL_INSTANCES
    position = instances[packed >> 3u].transform * position;
#endif
    return shadow_matrix(packed & 7u) * position;
}
