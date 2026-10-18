#define_import_path pbr/Common/reflection_history

#import pbr/frame_types

struct ReflectionHistoryPose {
    identity: vec4<u32>,
    state: vec4<u32>,
    current: mat4x4<f32>,
    previous: mat4x4<f32>,
    inverse_current: mat4x4<f32>,
}
struct ReflectionHistoryConfig {
    previous_vp: mat4x4<f32>,
    previous_eye: vec4<f32>,
    settings: vec4<f32>, // weight, normal cosine, roughness tolerance, ray bias
    tolerances: vec4<f32>, // position, hit, committed history enabled, reserved
}

fn history_world(pixel: vec2<i32>, z: f32, frame: Frame) -> vec3<f32> {
    let ndc = (vec2<f32>(pixel) + 0.5) / vec2<f32>(frame.size.xy) * vec2<f32>(2, -2) + vec2<f32>(-1, 1);
    let world = frame.inv_vp * vec4<f32>(ndc, z, 1);
    return world.xyz / world.w;
}
fn history_hit(position: vec3<f32>, normal: vec3<f32>, distance: f32, eye: vec3<f32>, bias: f32) -> vec3<f32> {
    return position + normal * bias + reflect(normalize(position - eye), normal) * distance;
}
fn history_valid(signal: vec4<f32>) -> bool {
    return signal.w > 0.0 && signal.w <= 1e18 && all(signal.rgb == signal.rgb) && all(signal.rgb >= vec3<f32>(0)) && all(signal.rgb <= vec3<f32>(1e18));
}
