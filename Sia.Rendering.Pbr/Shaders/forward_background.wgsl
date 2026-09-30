#define_import_path pbr/forward_background

#import pbr/frame_types

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var environment: texture_cube<f32>;
@group(0) @binding(2) var environment_sampler: sampler;
@group(0) @binding(3) var hdr: texture_storage_2d<rgba16float, write>;
@group(0) @binding(4) var depth: texture_depth_2d;

@compute @workgroup_size(8, 8)
fn forward_background(@builtin(global_invocation_id) id: vec3<u32>) {
    if (any(id.xy >= frame.size.xy)) { return; }
    if (textureLoad(depth, vec2<i32>(id.xy), 0) < 1.0) { return; }
    let ndc = (vec2<f32>(id.xy) + .5) / vec2<f32>(frame.size.xy) * vec2<f32>(2, -2) + vec2<f32>(-1, 1);
    let world = frame.inv_vp * vec4<f32>(ndc, 1, 1);
    let vector = world.xyz / world.w - frame.eye.xyz;
    let direction = vector * inverseSqrt(max(dot(vector, vector), 1e-20));
    let color = textureSampleLevel(environment, environment_sampler, direction, 0).rgb;
    textureStore(hdr, vec2<i32>(id.xy), vec4<f32>(color, 1));
}
