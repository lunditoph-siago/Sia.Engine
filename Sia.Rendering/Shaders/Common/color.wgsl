#define_import_path rendering/Common/color

// Independent scene-linear operations. The pipeline chooses its tone curve.
fn rendering_exposure(hdr: vec3<f32>, exposure: f32) -> vec3<f32> {
    return max(hdr * exposure, vec3<f32>(0));
}

fn rendering_aces_fitted(color: vec3<f32>) -> vec3<f32> {
    // The same rational curve, divided by x^2 for bright values to avoid Inf/Inf.
    // Positive overflow from display exposure has the correct saturated limit.
    let scaled = min(color, vec3<f32>(1));
    let inverse = 1.0 / max(color, vec3<f32>(1));
    return clamp((scaled * (2.51 * scaled + .03 * inverse)) /
        (scaled * (2.43 * scaled + .59 * inverse) + .14 * inverse * inverse), vec3<f32>(0), vec3<f32>(1));
}

fn rendering_linear_to_srgb(color: vec3<f32>) -> vec3<f32> {
    return select(1.055 * pow(color, vec3<f32>(1.0 / 2.4)) - .055, color * 12.92, color <= vec3<f32>(.0031308));
}
