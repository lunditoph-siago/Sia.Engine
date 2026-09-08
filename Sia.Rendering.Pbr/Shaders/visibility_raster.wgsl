#import pbr::visibility

struct RasterOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat, either) id: u32,
}
@vertex
fn vertex(@builtin(vertex_index) index: u32) -> RasterOutput {
    let work_index = index / 3u;
    let work = visibility_triangle_work(work_index);
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

fn shared_position(index: u32) -> vec4<f32> {
    let vertex = index >> 1u;
    let work = visibility_work[vertex / 256u];
    let offset = visibility_triangles[work.x].x;
    let source = visibility_vertices[visibility_indices[offset + vertex % 256u]].position_normal_x.xyz;
    return visibility_camera.view_projection * visibility_instances[work.y].transform * vec4<f32>(source, 1.0);
}

@vertex
fn indexed(@builtin(vertex_index) index: u32) -> RasterOutput {
    if ((index & 1u) == 0u) { return RasterOutput(shared_position(index), 0u); }
    let reference = index >> 1u;
    let id = reference / 3u;
    let work = visibility_triangle_work(id);
    let source = visibility_vertex(work.x, reference % 3u);
    return RasterOutput(visibility_camera.view_projection * visibility_instances[work.y].transform
        * vec4<f32>(source.position.xyz, 1.0), id + 1u);
}

@vertex
fn indexed_shadow(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    return shared_position(index);
}

struct FirstRasterOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat, first) id: u32,
}

@vertex
fn indexed_first(@builtin(vertex_index) index: u32) -> FirstRasterOutput {
    if ((index & 1u) == 0u) { return FirstRasterOutput(shared_position(index), 0u); }
    let id = index >> 1u;
    let work = visibility_triangle_work(id);
    let source = visibility_vertex(work.x, 0u);
    return FirstRasterOutput(visibility_camera.view_projection * visibility_instances[work.y].transform
        * vec4<f32>(source.position.xyz, 1.0), id + 1u);
}

@fragment
fn fragment_first(input: FirstRasterOutput) -> @location(0) u32 { return input.id; }
