#define_import_path rendering/Visibility/surface_data

struct VisibilitySurfaceData {
    normal_roughness: vec4<f32>,
    base_metallic: vec4<f32>,
}

fn rendering_surface_data(
    normal: vec3<f32>,
    roughness: f32,
    base_color: vec3<f32>,
    metallic: f32,
) -> VisibilitySurfaceData {
    return VisibilitySurfaceData(
        vec4<f32>(normal, clamp(roughness, 0.0, 1.0)),
        vec4<f32>(clamp(base_color, vec3<f32>(0.0), vec3<f32>(1.0)),
            clamp(metallic, 0.0, 1.0)),
    );
}
