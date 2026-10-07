#define_import_path pbr/reflection_temporal

#import pbr/frame_types
#import pbr/instance_types
#import pbr/reflection_encoding

struct HistoryFrame {
    previous_vp: mat4x4<f32>,
    previous_eye: vec4<f32>,
    parameters: vec4<f32>,
    state: vec4<u32>
}

@group(0) @binding(0) var<uniform> frame: Frame;
@group(1) @binding(1) var normal_roughness: texture_2d<f32>;
@group(1) @binding(2) var reflection_inputs: texture_2d<f32>;
@group(1) @binding(6) var raw: texture_2d<f32>;
@group(1) @binding(7) var history: texture_2d<f32>;
@group(1) @binding(8) var previous_world: texture_2d<f32>;
@group(1) @binding(9) var previous_normal: texture_2d<f32>;
@group(1) @binding(10) var<uniform> header: HistoryFrame;
@group(1) @binding(11) var<storage, read> current_instances: array<Instance>;
@group(1) @binding(12) var<storage, read> previous_instances: array<Instance>;

@vertex
fn reflection_vertex(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let p = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    return vec4<f32>(p * 2.0 - 1.0, 0, 1);
}

@fragment
fn reflection_temporal(@builtin(position) pixel: vec4<f32>) -> @location(0) vec4<f32> {
    let coordinate = vec2<i32>(pixel.xy);
    let signal = textureLoad(raw, coordinate, 0);
    // An old hit cannot fill a current miss, incomplete traversal or disocclusion.
    if (signal.w <= 0.0) { return vec4<f32>(0); }
    if (header.state.x == 0u) { return encode_reflection(signal); }
    let size = vec2<i32>(frame.size.xy);
    let receiver = textureLoad(reflection_inputs, coordinate + vec2<i32>(size.x, 0), 0);
    if (receiver.w < 1.0) { return encode_reflection(signal); }
    let slot = u32(receiver.w) - 1u;
    if (slot >= arrayLength(&current_instances) || slot >= arrayLength(&previous_instances)) { return encode_reflection(signal); }
    let surface = textureLoad(normal_roughness, coordinate, 0);
    let normal = normalize(surface.xyz);
    let expected_world = (previous_instances[slot].transform * transpose(current_instances[slot].normal_transform)
        * vec4<f32>(receiver.xyz, 1)).xyz;
    let expected_normal = normalize((previous_instances[slot].normal_transform * transpose(current_instances[slot].transform)
        * vec4<f32>(normal, 0)).xyz);
    let clip = header.previous_vp * vec4<f32>(expected_world, 1);
    if (clip.w <= 0.0 || clip.z < 0.0 || clip.z > clip.w) { return encode_reflection(signal); }
    let uv = clip.xy / clip.w * vec2<f32>(.5, -.5) + vec2<f32>(.5);
    if (any(uv < vec2<f32>(0)) || any(uv >= vec2<f32>(1))) { return encode_reflection(signal); }
    let sample_position = uv * vec2<f32>(size) - vec2<f32>(.5);
    let first = vec2<i32>(floor(sample_position));
    let fraction = fract(sample_position);
    let view_depth = abs((frame.view * vec4<f32>(receiver.xyz, 1)).z);
    let footprint = 2.0 * abs(frame.inv_proj[1].y) * view_depth / f32(size.y);
    let direction = reflect(normalize(receiver.xyz - frame.eye.xyz), normal);
    let hit_position = receiver.xyz + normal * .01 + direction * signal.w;
    let hit_tolerance = .02 + 4.0 * footprint * (1.0 + signal.w / max(view_depth, .01));
    var gathered = vec3<f32>(0);
    var gathered_weight = 0.0;
    for (var y = 0; y < 2; y++) {
        for (var x = 0; x < 2; x++) {
            let at = first + vec2<i32>(x, y);
            if (any(at < vec2<i32>(0)) || any(at >= size)) { continue; }
            let old_world = textureLoad(previous_world, at, 0);
            let old_surface = textureLoad(previous_normal, at, 0);
            let encoded = textureLoad(history, at, 0);
            if (encoded.w <= 0.0 || old_world.w != receiver.w || length(old_world.xyz - expected_world) > max(.01, footprint * 2.0)
                || dot(old_surface.xyz, expected_normal) < .95 || abs(old_surface.w - surface.w) > .05) { continue; }
            let old = decode_reflection(encoded);
            let old_direction = reflect(normalize(old_world.xyz - header.previous_eye.xyz), normalize(old_surface.xyz));
            let old_hit = old_world.xyz + normalize(old_surface.xyz) * .01 + old_direction * old.w;
            if (dot(old_direction, direction) < 1.0 - max(.002, surface.w * surface.w * .1)
                || length(old_hit - hit_position) > hit_tolerance) { continue; }
            let weight = select(1.0 - fraction.x, fraction.x, x == 1) * select(1.0 - fraction.y, fraction.y, y == 1);
            gathered += old.xyz * weight;
            gathered_weight += weight;
        }
    }
    if (gathered_weight <= .001) { return encode_reflection(signal); }
    // Clip in linear radiance, using only compatible current hits. Invalid misses are not black samples.
    var low = signal.xyz;
    var high = signal.xyz;
    for (var y = -1; y <= 1; y++) {
        for (var x = -1; x <= 1; x++) {
            let at = coordinate + vec2<i32>(x, y);
            if (any(at < vec2<i32>(0)) || any(at >= size)) { continue; }
            let adjacent = textureLoad(raw, at, 0);
            let adjacent_surface = textureLoad(normal_roughness, at, 0);
            let adjacent_receiver = textureLoad(reflection_inputs, at + vec2<i32>(size.x, 0), 0);
            if (adjacent.w <= 0.0 || adjacent_receiver.w != receiver.w || dot(adjacent_surface.xyz, normal) < .95
                || abs(adjacent_surface.w - surface.w) > .05) { continue; }
            low = min(low, adjacent.xyz);
            high = max(high, adjacent.xyz);
        }
    }
    let radiance = mix(signal.xyz, clamp(gathered / gathered_weight, low, high), header.parameters.x);
    return encode_reflection(vec4<f32>(radiance, signal.w));
}
