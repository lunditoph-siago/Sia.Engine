#define_import_path pbr/raster

#import pbr/bindings

struct StreamSelection {
    vp: mat4x4<f32>, table: vec4<u32>, screen: vec4<f32>, output: vec4<u32>
}

@group(2) @binding(12) var<storage, read> stream_work: array<u32>;
@group(2) @binding(13) var<uniform> stream_selection: StreamSelection;

struct RasterVertex {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat) ordinal: u32
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

@vertex
fn stream_vertex(@builtin(vertex_index) index: u32) -> RasterVertex {
    let ordinal = stream_work[index / 3u];
    return RasterVertex(frame.vp * world_corner(ordinal, index % 3u), ordinal);
}

@vertex
fn stream_shadow_vertex(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let ordinal = stream_work[index / 3u];
    return shadow_matrix(stream_selection.output.z) * world_corner(ordinal, index % 3u);
}

@vertex
fn shadow_vertex(
    @location(0) p: vec4<f32>,
    @builtin(instance_index) packed: u32
) -> @builtin(position) vec4<f32> {
    let position = vec4<f32>(p.xyz, 1.0);
    return shadow_matrix(packed & 7u) * position;
}
