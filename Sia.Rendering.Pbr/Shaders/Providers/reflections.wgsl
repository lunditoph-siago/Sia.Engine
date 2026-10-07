#define_import_path pbr/Providers/reflections

#import rendering/Environment/ibl

#if BAKED_REFLECTIONS
@group(0) @binding(14) var captured_environment: texture_cube<f32>;
// Capture position, influence minimum and maximum. Fourth components reserved.
@group(0) @binding(15) var<uniform> capture_box: array<vec4<f32>, 3>;
#endif

fn sample_environment_reflection(position: vec3<f32>, direction: vec3<f32>, roughness: f32) -> vec3<f32> {
    let sky = sample_prefiltered_specular(environment, environment_sampler, direction, roughness, f32(textureNumLevels(environment)));
#if BAKED_REFLECTIONS
    let minimum = capture_box[1].xyz;
    let maximum = capture_box[2].xyz;
    if (any(position <= minimum) || any(position >= maximum)) { return sky; }
    let safe_direction = select(vec3<f32>(1e-20), direction, abs(direction) > vec3<f32>(1e-20));
    let exits = max((minimum - position) / safe_direction, (maximum - position) / safe_direction);
    let distance = min(exits.x, min(exits.y, exits.z));
    let projected = position + direction * distance - capture_box[0].xyz;
    let local = sample_prefiltered_specular(captured_environment, environment_sampler, projected, roughness,
        f32(textureNumLevels(captured_environment)));
    let edge = min(position - minimum, maximum - position);
    let extent = maximum - minimum;
    let weight = smoothstep(0.0, .05 * min(extent.x, min(extent.y, extent.z)), min(edge.x, min(edge.y, edge.z)));
    return mix(sky, local, weight);
#else
    return sky;
#endif
}
