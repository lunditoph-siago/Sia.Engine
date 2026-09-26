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
    result.position = visibility_clip(source.position.xyz, work.y);
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
    let source = visibility_vertices[visibility_indices[offset + vertex % 256u]].xyz;
    return visibility_clip(source, work.y);
}

@vertex
fn indexed(@builtin(vertex_index) index: u32) -> RasterOutput {
    if ((index & 1u) == 0u) { return RasterOutput(shared_position(index), 0u, 0.0); }
    let reference = index >> 1u;
    let id = reference / 3u;
    let work = visibility_triangle_work(id);
    let source = visibility_vertex(work.x, reference % 3u);
    return RasterOutput(visibility_clip(source.position.xyz, work.y), id + 1u, visibility_instances[work.y].material.w);
}

@vertex
fn indexed_shadow(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
#ifdef WORLD_SPACE_GEOMETRY
    return visibility_clip(visibility_vertices[index].xyz, 0u);
#else
    return shared_position(index);
#endif
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
    return FirstRasterOutput(visibility_clip(source.position.xyz, work.y), id + 1u, visibility_instances[work.y].material.w);
}

@fragment
fn fragment_first(input: FirstRasterOutput, @builtin(front_facing) front: bool) -> @location(0) u32 {
    if (!front && input.double_sided == 0.0) { discard; }
    return input.id;
}

// Depth-only conservative proxy. The main view always uses the original mesh.
@vertex
fn shadow_bounds(@builtin(vertex_index) vertex: u32, @builtin(instance_index) id: u32) -> @builtin(position) vec4<f32> {
    let corners = array<u32, 36>(0u,2u,1u,1u,2u,3u, 4u,5u,6u,5u,7u,6u,
        0u,1u,4u,1u,5u,4u, 2u,6u,3u,3u,6u,7u, 0u,4u,2u,2u,4u,6u, 1u,3u,5u,3u,7u,5u);
    let source = visibility_instances[id];
    let lo = visibility_vertices[source.roots.w * 2u].xyz;
    let hi = visibility_vertices[source.roots.w * 2u + 1u].xyz;
    let corner = corners[vertex];
    let p = select(lo, hi, (vec3<u32>(corner) & vec3<u32>(1u,2u,4u)) != vec3<u32>(0u));
    return visibility_camera.view_projection * source.transform * vec4<f32>(p, 1.0);
}
