#define_import_path pbr/reflection_compose
#import pbr/Common/reflection_storage
#import pbr/frame_types

@group(0) @binding(0) var opaque: texture_2d_array<f32>;
@group(0) @binding(1) var specular_data: texture_2d_array<f32>;
@group(0) @binding(2) var signal: texture_2d_array<f32>;
@group(0) @binding(3) var hdr: texture_storage_2d<rgba16float, write>;
@group(0) @binding(4) var<uniform> frame: Frame;

@compute @workgroup_size(8, 8)
fn compose_reflections(@builtin(global_invocation_id) id: vec3<u32>) {
    if (any(id.xy >= textureDimensions(opaque))) { return; }
    let pixel = vec2<i32>(id.xy); var color = reflection_load_float(opaque, pixel, 0).rgb;
    let current = reflection_load_float(signal, pixel, 0);
    if (current.w > 0.0 && all(current.xyz == current.xyz) && all(current.xyz >= vec3<f32>(0)) && all(current.xyz <= vec3<f32>(1e18))) {
        let weight = reflection_load_float(specular_data, pixel, 0).rgb; let baseline = reflection_load_float(specular_data, pixel, 1).rgb;
        // Replace the exact current material's environment contribution; clip once, after signed composition.
        color += current.rgb * weight - baseline;
    }
    textureStore(hdr, pixel, vec4<f32>(clamp(color * frame.depth.z, vec3<f32>(0), vec3<f32>(65504)), 1));
}
