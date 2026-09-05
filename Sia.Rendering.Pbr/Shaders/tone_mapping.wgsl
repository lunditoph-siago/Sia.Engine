#import pbr::ibl::{ibl_fullscreen_ndc}

@group(0) @binding(0) var<uniform> settings: vec4<f32>;
@group(0) @binding(1) var hdr: texture_2d<f32>;

fn aces_fitted(color: vec3<f32>) -> vec3<f32> {
    let input_transform = mat3x3<f32>(
        vec3<f32>(0.59719, 0.07600, 0.02840),
        vec3<f32>(0.35458, 0.90834, 0.13383),
        vec3<f32>(0.04823, 0.01566, 0.83777));
    let output_transform = mat3x3<f32>(
        vec3<f32>(1.60475, -0.10208, -0.00327),
        vec3<f32>(-0.53108, 1.10813, -0.07276),
        vec3<f32>(-0.07367, -0.00605, 1.07602));
    let value = input_transform * color;
    let numerator = value * (value + vec3<f32>(0.0245786)) - vec3<f32>(0.000090537);
    let denominator = value * (0.983729 * value + vec3<f32>(0.4329510)) + vec3<f32>(0.238081);
    return clamp(output_transform * (numerator / denominator), vec3<f32>(0.0), vec3<f32>(1.0));
}

@vertex
fn vertex(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    return vec4<f32>(ibl_fullscreen_ndc(index), 0.0, 1.0);
}

@fragment
fn fragment(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let exposed = max(textureLoad(hdr, vec2<i32>(position.xy), 0).rgb * settings.x, vec3<f32>(0.0));
    var color = exposed / (vec3<f32>(1.0) + exposed);
    if (settings.z > 0.5) {
        color = aces_fitted(exposed);
    }
    if (settings.y > 0.5) {
        color = select(1.055 * pow(color, vec3<f32>(1.0 / 2.4)) - 0.055,
            color * 12.92, color <= vec3<f32>(0.0031308));
    }
    return vec4<f32>(color, 1.0);
}
