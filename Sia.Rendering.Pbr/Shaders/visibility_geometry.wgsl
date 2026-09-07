#define_import_path pbr::visibility

struct VisibilityCamera {
    view_projection: mat4x4<f32>,
    eye: vec4<f32>,
    size_counts: vec4<u32>,
    light_direction: vec4<f32>,
    light_radiance: vec4<f32>,
}
struct VisibilityVertex { position: vec4<f32>, normal: vec4<f32>, uv: vec4<f32> }
struct VisibilityInstance {
    transform: mat4x4<f32>,
    normal_transform: mat4x4<f32>,
    color: vec4<f32>,
    material: vec4<f32>,
    emissive: vec4<f32>,
    roots: vec4<u32>,
}
@group(0) @binding(0) var<uniform> visibility_camera: VisibilityCamera;
@group(0) @binding(1) var<storage, read> visibility_vertices: array<VisibilityVertex>;
@group(0) @binding(2) var<storage, read> visibility_meshlets: array<vec4<u32>>;
@group(0) @binding(3) var<storage, read> visibility_indices: array<u32>;
@group(0) @binding(4) var<storage, read> visibility_triangles: array<vec4<u32>>;
@group(0) @binding(5) var<storage, read> visibility_instances: array<VisibilityInstance>;
@group(0) @binding(6) var<storage, read> visibility_work: array<vec4<u32>>;

fn visibility_vertex(triangle: u32, corner: u32) -> VisibilityVertex {
    let reference = visibility_triangles[triangle];
    let cluster = visibility_meshlets[reference.x];
    let packed = visibility_indices[cluster.y + reference.y];
    let local = (packed >> (corner * 8u)) & 255u;
    return visibility_vertices[visibility_indices[cluster.x + local]];
}
