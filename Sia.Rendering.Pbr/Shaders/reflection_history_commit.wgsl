#define_import_path pbr/reflection_history_commit
#import pbr/Common/reflection_history

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<uniform> config: ReflectionHistoryConfig;
@group(0) @binding(2) var signal: texture_2d<f32>;
@group(0) @binding(3) var metadata: texture_2d_array<u32>;
@group(0) @binding(4) var specular: texture_2d_array<f32>;
@group(0) @binding(5) var normals: texture_2d<f32>;
@group(0) @binding(6) var depth: texture_depth_2d;
#if HISTORY_COMMIT_HITS
@group(0) @binding(7) var previous_hit: texture_storage_2d<rgba32float, write>;
#else
@group(0) @binding(7) var previous_signal: texture_storage_2d<rgba32float, write>;
@group(0) @binding(8) var previous_receiver: texture_storage_2d<rgba32float, write>;
@group(0) @binding(9) var previous_normal: texture_storage_2d<rgba16float, write>;
@group(0) @binding(10) var previous_metadata: texture_storage_2d_array<rgba32uint, write>;
#endif

@compute @workgroup_size(8, 8)
fn commit_reflections(@builtin(global_invocation_id) id: vec3<u32>) {
    if (any(id.xy >= frame.size.xy)) { return; }
    let pixel = vec2<i32>(id.xy); let value = textureLoad(signal, pixel, 0);
    var position = vec3<f32>(0); var normal = vec3<f32>(0); var roughness = 0.0;
    if (history_valid(value)) {
        position = history_world(pixel, textureLoad(depth, pixel, 0), frame);
        normal = normalize(textureLoad(normals, pixel, 0).xyz); roughness = textureLoad(specular, pixel, 1, 0).w;
    }
#if HISTORY_COMMIT_HITS
    var hit = vec4<f32>(0);
    if (history_valid(value)) { hit = vec4<f32>(history_hit(position, normal, value.w, frame.eye.xyz, config.settings.w), value.w); }
    textureStore(previous_hit, pixel, hit);
#else
    textureStore(previous_signal, pixel, value);
    textureStore(previous_receiver, pixel, vec4<f32>(position, roughness)); textureStore(previous_normal, pixel, vec4<f32>(normal, 0));
    for (var layer = 0; layer < 3; layer++) {
        var identity = vec4<u32>(0); if (history_valid(value)) { identity = textureLoad(metadata, pixel, layer, 0); }
        textureStore(previous_metadata, pixel, layer, identity);
    }
#endif
}
