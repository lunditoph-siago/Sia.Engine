#import pbr::visibility

struct RasterOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat, either) id: u32,
    @location(1) @interpolate(flat, either) double_sided: f32,
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
    result.double_sided = visibility_instances[work.y].material.w;
    return result;
}
@fragment
fn fragment(input: RasterOutput, @builtin(front_facing) front: bool) -> @location(0) u32 {
    if (!front && input.double_sided == 0.0) { discard; }
    return input.id;
}

struct DepthRasterOutput {
    @location(0) id: u32,
    @builtin(frag_depth) depth: f32,
}

@fragment
fn fragment_depth(input: RasterOutput, @builtin(front_facing) front: bool) -> DepthRasterOutput {
    if (!front && input.double_sided == 0.0) { discard; }
    return DepthRasterOutput(input.id, input.position.z);
}

fn shared_position(index: u32) -> vec4<f32> {
    let vertex = select(index >> 1u, index, visibility_camera.raster.w == 4u);
    let work = visibility_work[vertex / 256u];
    let offset = visibility_triangles[work.x].x;
    let source = visibility_vertices[visibility_indices[offset + vertex % 256u]].position_normal_x.xyz;
    return visibility_camera.view_projection * visibility_instances[work.y].transform * vec4<f32>(source, 1.0);
}

@vertex
fn indexed(@builtin(vertex_index) index: u32) -> RasterOutput {
    if ((index & 1u) == 0u) { return RasterOutput(shared_position(index), 0u, 0.0); }
    let reference = index >> 1u;
    let id = reference / 3u;
    let work = visibility_triangle_work(id);
    let source = visibility_vertex(work.x, reference % 3u);
    return RasterOutput(visibility_camera.view_projection * visibility_instances[work.y].transform
        * vec4<f32>(source.position.xyz, 1.0), id + 1u, visibility_instances[work.y].material.w);
}

@vertex
fn indexed_shadow(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    return shared_position(index);
}

struct FirstRasterOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) @interpolate(flat, first) id: u32,
    @location(1) @interpolate(flat, first) double_sided: f32,
}

@vertex
fn indexed_first(@builtin(vertex_index) index: u32) -> FirstRasterOutput {
    if ((index & 1u) == 0u) { return FirstRasterOutput(shared_position(index), 0u, 0.0); }
    let id = index >> 1u;
    let work = visibility_triangle_work(id);
    let source = visibility_vertex(work.x, 0u);
    return FirstRasterOutput(visibility_camera.view_projection * visibility_instances[work.y].transform
        * vec4<f32>(source.position.xyz, 1.0), id + 1u, visibility_instances[work.y].material.w);
}

@fragment
fn fragment_first(input: FirstRasterOutput, @builtin(front_facing) front: bool) -> @location(0) u32 {
    if (!front && input.double_sided == 0.0) { discard; }
    return input.id;
}
