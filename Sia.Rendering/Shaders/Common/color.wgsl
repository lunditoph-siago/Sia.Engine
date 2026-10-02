#define_import_path rendering/Common/color

// Independent scene-linear operations. The pipeline chooses its tone curve.
fn rendering_exposure(hdr: vec3<f32>, exposure: f32) -> vec3<f32> {
    return max(hdr * exposure, vec3<f32>(0));
}

fn rendering_aces_fitted(color: vec3<f32>) -> vec3<f32> {
    return clamp((color * (2.51 * color + .03)) / (color * (2.43 * color + .59) + .14), vec3<f32>(0), vec3<f32>(1));
}

fn rendering_linear_to_srgb(color: vec3<f32>) -> vec3<f32> {
    return select(1.055 * pow(color, vec3<f32>(1.0 / 2.4)) - .055, color * 12.92, color <= vec3<f32>(.0031308));
}
