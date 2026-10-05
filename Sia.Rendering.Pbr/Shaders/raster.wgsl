#define_import_path pbr/raster

#import pbr/bindings
#import pbr/stream_selection_types

@group(2) @binding(12) var<storage, read> stream_work: array<vec2<u32>>;
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

@vertex
fn stream_vertex(@builtin(vertex_index) index: u32) -> RasterVertex {
#if STREAM_INSTANCES
    let ordinal = index / 3u;
    let work = stream_work[ordinal];
    let position = instances[work.y].transform * world_corner(work.x, index % 3u);
    return RasterVertex(frame.vp * position, ordinal);
#else
    return RasterVertex(vec4<f32>(0.0), 0u);
#endif
}

@vertex
fn stream_shadow_vertex(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
#if STREAM_INSTANCES
    let work = stream_work[index / 3u];
    let position = instances[work.y].transform * world_corner(work.x, index % 3u);
    return shadow_matrix(stream_selection.output.z) * position;
#else
    return vec4<f32>(0.0);
#endif
}

@vertex
fn shadow_vertex(
    @location(0) p: vec4<f32>,
    @builtin(instance_index) packed: u32
) -> @builtin(position) vec4<f32> {
    let position = vec4<f32>(p.xyz, 1.0);
    return shadow_matrix(packed & 7u) * position;
}
