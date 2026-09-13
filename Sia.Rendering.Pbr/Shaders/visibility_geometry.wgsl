#define_import_path pbr::visibility

struct VisibilityCamera {
    view_projection: mat4x4<f32>,
    eye: vec4<f32>,
    size_counts: vec4<u32>,
    light_direction: vec4<f32>,
    light_radiance: vec4<f32>,
    raster: vec4<u32>,
    raster_origin: vec4<f32>,
}
struct VisibilityVertex { position: vec4<f32>, normal: vec4<f32>, uv: vec4<f32>, tangent: vec4<f32> }
struct VisibilityInstance {
    transform: mat4x4<f32>,
    normal_transform: mat4x4<f32>,
    color: vec4<f32>,
    material: vec4<f32>,
    emissive: vec4<f32>,
    roots: vec4<u32>,
}
@group(0) @binding(0) var<uniform> visibility_camera: VisibilityCamera;
@group(0) @binding(1) var<storage, read> visibility_vertices: array<vec4<f32>>;
@group(0) @binding(3) var<storage, read> visibility_indices: array<u32>;
@group(0) @binding(4) var<storage, read> visibility_triangles: array<vec2<u32>>;
@group(0) @binding(5) var<storage, read> visibility_instances: array<VisibilityInstance>;
@group(0) @binding(6) var<storage, read> visibility_work: array<vec2<u32>>;

fn visibility_clip(position: vec3<f32>, instance: u32) -> vec4<f32> {
#ifdef WORLD_SPACE_GEOMETRY
    return visibility_camera.view_projection * vec4<f32>(position, 1.0);
#else
    return visibility_camera.view_projection * visibility_instances[instance].transform * vec4<f32>(position, 1.0);
#endif
}

fn visibility_triangle_work(index: u32) -> vec2<u32> {
    let shift = visibility_camera.raster.x;
    let work = visibility_work[index >> shift];
    return vec2<u32>(work.x + (index & ((1u << shift) - 1u)), work.y);
}

fn visibility_vertex(triangle: u32, corner: u32) -> VisibilityVertex {
    let reference = visibility_triangles[triangle];
    let local = (reference.y >> (corner * 8u)) & 255u;
    let index = visibility_indices[reference.x + local];
    let count = arrayLength(&visibility_vertices) / 3u;
    let position = visibility_vertices[index];
    let attributes = visibility_vertices[count + index];
    return VisibilityVertex(vec4<f32>(position.xyz, 0.0),
        vec4<f32>(position.w, attributes.xy, 0.0),
        vec4<f32>(attributes.zw, 0.0, 0.0), visibility_vertices[count * 2u + index]);
}
