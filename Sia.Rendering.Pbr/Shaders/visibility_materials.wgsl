#define_import_path pbr::materials

struct VisibilityMaterial {
    color_metallic: vec4<f32>,
    emissive_roughness: vec4<f32>,
    texture_factors: vec4<f32>,
    layers: vec4<u32>,
    indices: vec4<u32>,
}
