#import pbr::visibility

struct RasterOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat) id: u32,
}
@vertex
fn vertex(@builtin(vertex_index) index: u32) -> RasterOutput {
    let work_index = index / 3u;
    let work = visibility_work[work_index];
    let source = visibility_vertex(work.x, index % 3u);
    var result: RasterOutput;
    result.position = visibility_camera.view_projection * visibility_instances[work.y].transform
        * vec4<f32>(source.position.xyz, 1.0);
    result.id = work_index + 1u;
    return result;
}
@fragment
fn fragment(input: RasterOutput) -> @location(0) u32 {
    return input.id;
}
