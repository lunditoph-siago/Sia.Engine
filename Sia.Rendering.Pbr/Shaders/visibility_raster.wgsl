#import pbr::visibility

struct RasterOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat) id: u32,
}
@vertex
fn vertex(@builtin(vertex_index) index: u32, @builtin(instance_index) instance: u32) -> RasterOutput {
    let triangle = index / 3u;
    let source = visibility_vertex(triangle, index % 3u);
    var result: RasterOutput;
    result.position = visibility_camera.view_projection * visibility_instances[instance].transform
        * vec4<f32>(source.position.xyz, 1.0);
    result.id = instance * visibility_camera.size_counts.z + triangle + 1u;
    return result;
}
@fragment
fn fragment(input: RasterOutput) -> @location(0) u32 {
    return input.id;
}
