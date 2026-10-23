#define_import_path pbr/reflection_history_commit
#import pbr/Common/reflection_storage
#import pbr/Common/reflection_history

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<uniform> config: ReflectionHistoryConfig;
@group(0) @binding(2) var signal: texture_2d_array<f32>;
@group(0) @binding(3) var metadata: texture_2d_array<f32>;
@group(0) @binding(4) var specular: texture_2d_array<f32>;
@group(0) @binding(5) var normals: texture_2d<f32>;
@group(0) @binding(6) var depth: texture_2d<f32>;
#if HISTORY_COMMIT_HITS
@group(0) @binding(7) var previous_hit: texture_storage_2d_array<rgba8unorm, write>;
#else
@group(0) @binding(7) var previous_signal: texture_storage_2d_array<rgba8unorm, write>;
@group(0) @binding(8) var previous_receiver: texture_storage_2d_array<rgba8unorm, write>;
@group(0) @binding(9) var previous_normal: texture_storage_2d<rgba16float, write>;
@group(0) @binding(10) var previous_metadata: texture_storage_2d_array<rgba8unorm, write>;
#endif

#if HISTORY_COMMIT_HITS
fn commit_store_hit(pixel: vec2<i32>, layer: i32, value: vec4<f32>) {
    for (var plane = 0u; plane < 4u; plane++) {
        textureStore(previous_hit, pixel, layer * 4 + i32(plane), reflection_pack_float(value, plane));
    }
}
#else
fn commit_store_signal(pixel: vec2<i32>, layer: i32, value: vec4<f32>) {
    for (var plane = 0u; plane < 4u; plane++) {
        textureStore(previous_signal, pixel, layer * 4 + i32(plane), reflection_pack_float(value, plane));
    }
}

fn commit_store_receiver(pixel: vec2<i32>, layer: i32, value: vec4<f32>) {
    for (var plane = 0u; plane < 4u; plane++) {
        textureStore(previous_receiver, pixel, layer * 4 + i32(plane), reflection_pack_float(value, plane));
    }
}

fn commit_store_metadata(pixel: vec2<i32>, layer: i32, value: vec4<u32>) {
    for (var plane = 0u; plane < 4u; plane++) {
        textureStore(previous_metadata, pixel, layer * 4 + i32(plane), reflection_pack_bits(value, plane));
    }
}
#endif

@compute @workgroup_size(8, 8)
fn commit_reflections(@builtin(global_invocation_id) id: vec3<u32>) {
    if (any(id.xy >= frame.size.xy)) { return; }
    let pixel = vec2<i32>(id.xy); let value = reflection_load_float(signal, pixel, 0);
    var position = vec3<f32>(0); var normal = vec3<f32>(0); var roughness = 0.0;
    if (history_valid(value)) {
        position = history_world(pixel, textureLoad(depth, pixel, 0).x, frame);
        normal = normalize(textureLoad(normals, pixel, 0).xyz); roughness = reflection_load_float(specular, pixel, 1).w;
    }
#if HISTORY_COMMIT_HITS
    var hit = vec4<f32>(0);
    if (history_valid(value)) { hit = vec4<f32>(history_hit(position, normal, value.w, frame.eye.xyz, config.settings.w), value.w); }
    commit_store_hit(pixel, 0, hit);
#else
    commit_store_signal(pixel, 0, value);
    commit_store_receiver(pixel, 0, vec4<f32>(position, roughness)); textureStore(previous_normal, pixel, vec4<f32>(normal, 0));
    for (var layer = 0; layer < 3; layer++) {
        var identity = vec4<u32>(0); if (history_valid(value)) { identity = reflection_load_bits(metadata, pixel, layer); }
        commit_store_metadata(pixel, layer, identity);
    }
#endif
}
