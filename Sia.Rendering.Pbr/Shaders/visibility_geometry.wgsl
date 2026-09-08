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
struct PackedVisibilityVertex { position_normal_x: vec4<f32>, normal_yz_uv: vec4<f32>, tangent: vec4<f32> }
struct VisibilityInstance {
    transform: mat4x4<f32>,
    normal_transform: mat4x4<f32>,
    color: vec4<f32>,
    material: vec4<f32>,
    emissive: vec4<f32>,
    roots: vec4<u32>,
}
@group(0) @binding(0) var<uniform> visibility_camera: VisibilityCamera;
@group(0) @binding(1) var<storage, read> visibility_vertices: array<PackedVisibilityVertex>;
@group(0) @binding(3) var<storage, read> visibility_indices: array<u32>;
@group(0) @binding(4) var<storage, read> visibility_triangles: array<vec2<u32>>;
@group(0) @binding(5) var<storage, read> visibility_instances: array<VisibilityInstance>;
@group(0) @binding(6) var<storage, read> visibility_work: array<vec2<u32>>;

fn visibility_triangle_work(index: u32) -> vec2<u32> {
    let stride = visibility_camera.raster.x;
    let work = visibility_work[index / stride];
    return vec2<u32>(work.x + index % stride, work.y);
}

fn visibility_vertex(triangle: u32, corner: u32) -> VisibilityVertex {
    let reference = visibility_triangles[triangle];
    let local = (reference.y >> (corner * 8u)) & 255u;
    let vertex = visibility_vertices[visibility_indices[reference.x + local]];
    return VisibilityVertex(vec4<f32>(vertex.position_normal_x.xyz, 0.0),
        vec4<f32>(vertex.position_normal_x.w, vertex.normal_yz_uv.xy, 0.0),
        vec4<f32>(vertex.normal_yz_uv.zw, 0.0, 0.0), vertex.tangent);
}
