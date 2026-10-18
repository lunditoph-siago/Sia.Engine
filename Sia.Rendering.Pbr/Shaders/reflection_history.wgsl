#define_import_path pbr/reflection_history
#import pbr/Common/reflection_history

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<uniform> config: ReflectionHistoryConfig;
@group(0) @binding(2) var<storage, read> poses: array<ReflectionHistoryPose>;
@group(0) @binding(3) var<storage, read> receiver_poses: array<u32>;
@group(0) @binding(4) var signal: texture_2d<f32>;
@group(0) @binding(5) var metadata: texture_2d_array<u32>;
@group(0) @binding(6) var specular: texture_2d_array<f32>;
@group(0) @binding(7) var normals: texture_2d<f32>;
@group(0) @binding(8) var depth: texture_depth_2d;
@group(0) @binding(9) var previous_signal: texture_2d<f32>;
@group(0) @binding(10) var previous_receiver: texture_2d<f32>;
@group(0) @binding(11) var previous_normal: texture_2d<f32>;
@group(0) @binding(12) var previous_metadata: texture_2d_array<u32>;
@group(0) @binding(13) var previous_hit: texture_2d<f32>;
@group(0) @binding(14) var filtered: texture_storage_2d<rgba32float, write>;

fn history_pose_valid(pose: ReflectionHistoryPose) -> bool {
    return pose.state.x != 0u && pose.state.y != 0u && pose.state.z != 0u;
}

@compute @workgroup_size(8, 8)
fn filter_reflections(@builtin(global_invocation_id) id: vec3<u32>) {
    if (any(id.xy >= frame.size.xy)) { return; }
    let pixel = vec2<i32>(id.xy); let current = textureLoad(signal, pixel, 0);
    textureStore(filtered, pixel, current);
    // Current misses and unproven traces always win over old visibility.
    if (!history_valid(current) || config.tolerances.z < 0.5 || frame.size.w != 0u) { return; }
    let indices = textureLoad(metadata, pixel, 0, 0);
    if (indices.x >= arrayLength(&poses) || indices.z >= arrayLength(&receiver_poses) || receiver_poses[indices.z] >= arrayLength(&poses)) { return; }
    let receiver = poses[receiver_poses[indices.z]]; let hit_pose = poses[indices.x];
    if (!history_pose_valid(receiver) || !history_pose_valid(hit_pose)) { return; }
    let receiver_id = textureLoad(metadata, pixel, 1, 0); let hit_id = textureLoad(metadata, pixel, 2, 0);
    if (all(receiver_id == vec4<u32>(0)) || all(hit_id == vec4<u32>(0)) || any(receiver_id != receiver.identity) || any(hit_id != hit_pose.identity)) { return; }
    let position = history_world(pixel, textureLoad(depth, pixel, 0), frame); let normal = normalize(textureLoad(normals, pixel, 0).xyz);
    let roughness = textureLoad(specular, pixel, 1, 0).w;
    let old_position = (receiver.previous * receiver.inverse_current * vec4<f32>(position, 1)).xyz;
    let old_normal = normalize((receiver.previous * receiver.inverse_current * vec4<f32>(normal, 0)).xyz);
    let clip = config.previous_vp * vec4<f32>(old_position, 1);
    if (clip.w <= 0.0) { return; }
    let ndc = clip.xyz / clip.w; let uv = ndc.xy * vec2<f32>(0.5, -0.5) + 0.5;
    if (any(uv < vec2<f32>(0)) || any(uv >= vec2<f32>(1)) || ndc.z < 0.0 || ndc.z > 1.0) { return; }
    let old_pixel = vec2<i32>(floor(uv * vec2<f32>(frame.size.xy)));
    let old_signal = textureLoad(previous_signal, old_pixel, 0);
    if (!history_valid(old_signal) || any(textureLoad(previous_metadata, old_pixel, 1, 0) != receiver_id)
        || any(textureLoad(previous_metadata, old_pixel, 2, 0) != hit_id) || textureLoad(previous_metadata, old_pixel, 0, 0).y != indices.y) { return; }
    let old_surface = textureLoad(previous_receiver, old_pixel, 0); let old_surface_normal = normalize(textureLoad(previous_normal, old_pixel, 0).xyz);
    let position_tolerance = config.tolerances.x;
    if (distance(old_position, old_surface.xyz) > position_tolerance || dot(old_normal, old_surface_normal) < config.settings.y
        || abs(roughness - old_surface.w) > config.settings.z) { return; }
    let point = history_hit(position, normal, current.w, frame.eye.xyz, config.settings.w);
    let old_point = (hit_pose.previous * hit_pose.inverse_current * vec4<f32>(point, 1)).xyz;
    let stored_hit = textureLoad(previous_hit, old_pixel, 0);
    let old_origin = old_position + old_normal * config.settings.w;
    if (distance(old_point, stored_hit.xyz) > config.tolerances.y
        || abs(distance(old_point, old_origin) - old_signal.w) > config.tolerances.y) { return; }
    var minimum = current.rgb; var maximum = current.rgb;
    // Clip in linear radiance, using only compatible current samples.
    for (var y = -1; y <= 1; y++) { for (var x = -1; x <= 1; x++) {
        let neighbor = pixel + vec2<i32>(x, y);
        if (any(neighbor < vec2<i32>(0)) || any(neighbor >= vec2<i32>(frame.size.xy))) { continue; }
        let value = textureLoad(signal, neighbor, 0);
        if (!history_valid(value) || any(textureLoad(metadata, neighbor, 1, 0) != receiver_id)
            || any(textureLoad(metadata, neighbor, 2, 0) != hit_id) || textureLoad(metadata, neighbor, 0, 0).y != indices.y) { continue; }
        if (dot(normalize(textureLoad(normals, neighbor, 0).xyz), normal) < config.settings.y
            || abs(textureLoad(specular, neighbor, 1, 0).w - roughness) > config.settings.z
            || distance(history_world(neighbor, textureLoad(depth, neighbor, 0), frame), position) > position_tolerance * 4.0) { continue; }
        minimum = min(minimum, value.rgb); maximum = max(maximum, value.rgb);
    } }
    textureStore(filtered, pixel, vec4<f32>(mix(current.rgb, clamp(old_signal.rgb, minimum, maximum), config.settings.x), current.w));
}
