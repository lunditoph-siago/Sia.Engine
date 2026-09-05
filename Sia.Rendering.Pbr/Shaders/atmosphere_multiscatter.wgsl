#import pbr::atmosphere::{atmosphere, integrate_atmosphere, ATM_PI}

@group(1) @binding(0) var output_lut: texture_storage_2d<rgba16float, write>;
var<workgroup> radiance_sum: array<vec3<f32>, 64>;
var<workgroup> feedback_sum: array<vec3<f32>, 64>;

@compute @workgroup_size(1, 1, 64)
fn compute(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let size = textureDimensions(output_lut);
    let uv = vec2<f32>(group.xy) / vec2<f32>(size - 1u);
    let radius = mix(atmosphere.radii.x + 0.001, atmosphere.radii.y - 0.001, uv.y);
    let mu = uv.x * 2.0 - 1.0;
    let sun = vec3<f32>(sqrt(max(1.0 - mu * mu, 0.0)), mu, 0.0);
    let z = 1.0 - 2.0 * (f32(lane % 8u) + 0.5) / 8.0;
    let phi = 2.0 * ATM_PI * (f32(lane / 8u) + 0.5) / 8.0;
    let direction = vec3<f32>(cos(phi) * sqrt(1.0 - z * z), z, sin(phi) * sqrt(1.0 - z * z));
    let value = integrate_atmosphere(vec3<f32>(0.0, radius, 0.0), direction, sun, 1e9, 32u, true, true);
    radiance_sum[lane] = value.radiance / 64.0;
    feedback_sum[lane] = value.feedback / 64.0;
    workgroupBarrier();
    for (var stride = 32u; stride > 0u; stride /= 2u) {
        if (lane < stride) {
            radiance_sum[lane] += radiance_sum[lane + stride];
            feedback_sum[lane] += feedback_sum[lane + stride];
        }
        workgroupBarrier();
    }
    if (lane == 0u) {
        let value = radiance_sum[0] / max(vec3<f32>(1.0) - feedback_sum[0], vec3<f32>(1e-4));
        textureStore(output_lut, group.xy, vec4<f32>(value, 1.0));
    }
}
