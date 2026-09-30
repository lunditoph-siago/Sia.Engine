#define_import_path pbr/bindings

#import pbr/frame_types

struct Material {
    color: vec4<f32>,
    emission: vec4<f32>,
    factors: vec4<f32>,
    layers: vec4<u32>,
    indices: vec4<u32>,
    transport: vec4<f32>,
}

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<storage, read> scene_data: array<vec4<f32>>;
#if WRITABLE_CLUSTERS
@group(0) @binding(2) var<storage, read_write> clusters: array<u32>;
#else
@group(0) @binding(2) var<storage, read> clusters: array<u32>;
#endif
@group(0) @binding(3) var shadow_atlas: texture_depth_2d_array;
@group(0) @binding(4) var environment: texture_cube<f32>;
@group(0) @binding(5) var brdf_lut: texture_2d<f32>;
@group(0) @binding(6) var environment_sampler: sampler;
@group(0) @binding(7) var brdf_sampler: sampler;
@group(0) @binding(8) var<uniform> sh: array<vec4<f32>, 9>;
#if SCENE_GI
@group(0) @binding(9) var probe_volume: texture_3d<f32>;
@group(0) @binding(10) var<uniform> probe_header: array<vec4<f32>,3>;
#endif
@group(1) @binding(0) var<storage, read> vertices: array<vec4<f32>>;
@group(1) @binding(1) var<storage, read> topology: array<u32>;
@group(2) @binding(0) var<storage, read> materials: array<Material>;
@group(2) @binding(1) var<uniform> batch: vec4<u32>;
@group(2) @binding(2) var albedo: texture_2d_array<f32>;
@group(2) @binding(3) var albedo_sampler: sampler;
@group(2) @binding(4) var normal_map: texture_2d_array<f32>;
@group(2) @binding(5) var normal_sampler: sampler;
@group(2) @binding(6) var mr_map: texture_2d_array<f32>;
@group(2) @binding(7) var mr_sampler: sampler;
@group(2) @binding(8) var ao_map: texture_2d_array<f32>;
@group(2) @binding(9) var ao_sampler: sampler;
@group(2) @binding(10) var emission_map: texture_2d_array<f32>;
@group(2) @binding(11) var emission_sampler: sampler;

fn unit(v: vec3<f32>) -> vec3<f32> {
    return v * inverseSqrt(max(dot(v, v), 1e-20));
}

fn shadow_matrix(layer: u32) -> mat4x4<f32> {
    let i = frame.counts.w + layer * 4u;
    return mat4x4<f32>(scene_data[i], scene_data[i + 1u], scene_data[i + 2u], scene_data[i + 3u]);
}

fn vertex_index(triangle: u32, corner: u32) -> u32 {
    return topology[triangle * 3u + corner];
}

struct Vertex {
    position: vec3<f32>,
    normal: vec3<f32>,
    uv: vec2<f32>,
    tangent: vec4<f32>
}

fn vertex_data(triangle: u32, corner: u32) -> Vertex {
    let index = vertex_index(triangle, corner);
    let p = vertices[index];
    let n = vertices[frame.geometry.x + index];
    return Vertex(p.xyz, vec3<f32>(p.w, n.xy), n.zw, vertices[frame.geometry.x * 2u + index]);
}

fn world_corner(triangle: u32, corner: u32) -> vec4<f32> {
    return vec4<f32>(vertices[vertex_index(triangle, corner)].xyz, 1.0);
}

fn triangle_material(triangle: u32) -> u32 {
    return u32(abs(vertices[frame.geometry.x * 2u + vertex_index(triangle, 0u)].w)) - 1u;
}
