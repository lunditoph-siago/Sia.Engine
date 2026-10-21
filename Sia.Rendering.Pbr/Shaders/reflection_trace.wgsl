#define_import_path pbr/reflection_trace
#define INSTANCE_TRACE_FILTER 1

#import pbr/frame_types
#import rendering/Geometry/instance_trace
#import rendering/Lighting/local_light
#import rendering/Lighting/probe_volume

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<storage, read> scene_lights: array<vec4<f32>>;
@group(0) @binding(2) var<storage, read> trace_surfaces: array<vec4<f32>>;
@group(0) @binding(3) var<storage, read> instance_trace_geometry: array<vec4<u32>>;
@group(0) @binding(4) var<storage, read> instance_trace_instances: array<vec4<u32>>;
@group(0) @binding(5) var<uniform> config: array<vec4<f32>, 2>;
@group(0) @binding(6) var depth: texture_depth_2d;
@group(0) @binding(7) var normal_roughness: texture_2d<f32>;
@group(0) @binding(8) var specular_data: texture_2d_array<f32>;
@group(0) @binding(9) var signal: texture_storage_2d<rgba32float, write>;
@group(0) @binding(10) var probe_volume: texture_3d<f32>;
@group(0) @binding(11) var probe_sampler: sampler;
@group(0) @binding(12) var<uniform> probe_header: array<vec4<f32>, 3>;
@group(0) @binding(13) var<uniform> sh: array<vec4<f32>, 9>;
@group(0) @binding(14) var hit_metadata: texture_storage_2d_array<rgba32uint, write>;
struct ReflectionPose {
    identity: vec4<u32>,
    state: vec4<u32>,
    current: mat4x4<f32>,
    previous: mat4x4<f32>,
    inverse_current: mat4x4<f32>,
}
@group(0) @binding(15) var<storage, read> reflection_poses: array<ReflectionPose>;
@group(0) @binding(16) var<storage, read> receiver_poses: array<u32>;

fn reflection_metadata(pixel: vec2<i32>, hit: InstanceTraceHit, receiver: u32) {
    textureStore(hit_metadata, pixel, 0, vec4<u32>(hit.instance, hit.primitive, receiver, 1u));
    if (receiver < arrayLength(&receiver_poses) && receiver_poses[receiver] < arrayLength(&reflection_poses)) {
        textureStore(hit_metadata, pixel, 1, reflection_poses[receiver_poses[receiver]].identity);
    }
    if (hit.instance < arrayLength(&reflection_poses)) { textureStore(hit_metadata, pixel, 2, reflection_poses[hit.instance].identity); }
}

fn reflection_finite(value: vec3<f32>) -> bool {
    return all(value == value) && all(abs(value) <= vec3<f32>(3.402823466e38));
}

fn instance_trace_accept_material(material: u32, reference: bool) -> bool {
    return material < arrayLength(&trace_surfaces) / 2u && trace_surfaces[material * 2u + 1u].w < 0.5;
}

fn reflection_shadow(origin: vec3<f32>, direction: vec3<f32>, maximum: f32) -> vec2<bool> {
    let query = trace_instances_bounded(origin, direction, config[0].y, maximum, false, u32(config[1].y));
    return vec2<bool>(query.complete, query.hit.instance == 0xffffffffu);
}

fn reflection_local(index: u32, position: vec3<f32>, normal: vec3<f32>, albedo: vec3<f32>) -> vec3<f32> {
    let p = scene_lights[index * 4u]; let d = scene_lights[index * 4u + 1u];
    let radiance = scene_lights[index * 4u + 2u].rgb; let cone = scene_lights[index * 4u + 3u];
    let delta = p.xyz - position; let distance = length(delta);
    if (distance <= config[0].y * 2.0 || distance >= p.w || distance > config[0].x) { return vec3<f32>(0); }
    let direction = delta / distance; var attenuation = rendering_range_attenuation(distance, p.w);
    if (d.w > 0.5) { attenuation *= rendering_spot_attenuation(dot(-direction, normalize(d.xyz)), cone.x, cone.y); }
    return albedo * radiance * (max(dot(normal, direction), 0.0) * attenuation / 3.14159265359);
}

@compute @workgroup_size(8, 8)
fn trace_reflections(@builtin(global_invocation_id) id: vec3<u32>) {
    if (any(id.xy >= frame.size.xy)) { return; }
    let pixel = vec2<i32>(id.xy);
    textureStore(signal, pixel, vec4<f32>(0));
    textureStore(hit_metadata, pixel, 0, vec4<u32>(0xffffffffu));
    textureStore(hit_metadata, pixel, 1, vec4<u32>(0)); textureStore(hit_metadata, pixel, 2, vec4<u32>(0));
    let weight = textureLoad(specular_data, pixel, 0, 0); let baseline = textureLoad(specular_data, pixel, 1, 0);
    let surface = textureLoad(normal_roughness, pixel, 0);
    if (frame.size.w != 0u || weight.w < 1.0 || !any(weight.rgb > vec3<f32>(0)) || baseline.w > config[0].z || all(surface.xyz == vec3<f32>(0))) { return; }
    let z = textureLoad(depth, pixel, 0);
    let ndc = (vec2<f32>(id.xy) + 0.5) / vec2<f32>(frame.size.xy) * vec2<f32>(2, -2) + vec2<f32>(-1, 1);
    let world = frame.inv_vp * vec4<f32>(ndc, z, 1);
    let receiver = world.xyz / world.w; let receiver_normal = normalize(surface.xyz);
    let direction = reflect(normalize(receiver - frame.eye.xyz), receiver_normal);
    let origin = receiver + receiver_normal * config[0].y;
    let query = trace_instances_bounded(origin, direction, config[0].y, config[0].x, false, u32(config[1].x));
    if (!query.complete) { textureStore(signal, pixel, vec4<f32>(0, 0, 0, -1)); return; }
    if (query.hit.instance == 0xffffffffu) { return; }
    let hit = query.hit; let material = hit.material;
    let albedo = trace_surfaces[material * 2u]; let emission = trace_surfaces[material * 2u + 1u].rgb;
    // A one-sided back-face hit is a valid black boundary, never sky leakage.
    if (!hit.front && albedo.w < 0.5) {
        textureStore(signal, pixel, vec4<f32>(0, 0, 0, hit.parameter));
        reflection_metadata(pixel, hit, u32(weight.w) - 1u); return;
    }
    if (!any(albedo.rgb > vec3<f32>(0))) {
        textureStore(signal, pixel, vec4<f32>(emission, hit.parameter));
        reflection_metadata(pixel, hit, u32(weight.w) - 1u); return;
    }
    let normal = select(-hit.normal, hit.normal, hit.front); let position = origin + direction * hit.parameter;
    let biased = position + normal * config[0].y;
    let irradiance = rendering_scene_probe_irradiance(probe_volume, probe_sampler, probe_header, sh, position, normal, false);
    var radiance = emission + albedo.rgb * irradiance / 3.14159265359;
    for (var at = 0u; at < min(frame.counts.y, 4u); at++) {
        let toward = normalize(-frame.directional[at].direction.xyz); let cosine = max(dot(normal, toward), 0.0);
        if (cosine <= 0.0 || !any(frame.directional[at].radiance.rgb > vec3<f32>(0)) || !any(albedo.rgb > vec3<f32>(0))) { continue; }
        let shadow = reflection_shadow(biased, toward, config[0].x);
        if (!shadow.x) { textureStore(signal, pixel, vec4<f32>(0, 0, 0, -1)); return; }
        if (shadow.y) { radiance += albedo.rgb * frame.directional[at].radiance.rgb * (cosine / 3.14159265359); }
    }
    var selected: array<u32, 8>; var energy: array<f32, 8>; var contributions: array<vec3<f32>, 8>;
    for (var slot = 0u; slot < 8u; slot++) { selected[slot] = 0xffffffffu; }
    let limit = min(u32(config[1].z), 8u);
    // Rank offscreen lights from the full bounded table, independently of the receiver's screen-space clusters.
    for (var at = 0u; limit > 0u && at < min(frame.counts.x, 256u); at++) {
        let contribution = reflection_local(at, position, normal, albedo.rgb);
        if (!reflection_finite(contribution)) { textureStore(signal, pixel, vec4<f32>(0, 0, 0, -1)); return; }
        let priority = max(contribution.x, max(contribution.y, contribution.z));
        if (priority <= 0.0) { continue; }
        for (var slot = 0u; slot < limit; slot++) {
            if (priority <= energy[slot]) { continue; }
            for (var shift = limit - 1u; shift > slot; shift--) {
                selected[shift] = selected[shift - 1u]; energy[shift] = energy[shift - 1u]; contributions[shift] = contributions[shift - 1u];
            }
            selected[slot] = at; energy[slot] = priority; contributions[slot] = contribution; break;
        }
    }
    for (var slot = 0u; slot < limit; slot++) {
        if (selected[slot] == 0xffffffffu) { continue; }
        let delta = scene_lights[selected[slot] * 4u].xyz - biased; let distance = length(delta);
        if (distance <= 2.0 * config[0].y) { continue; }
        let shadow = reflection_shadow(biased, delta / distance, distance - config[0].y);
        if (!shadow.x) { textureStore(signal, pixel, vec4<f32>(0, 0, 0, -1)); return; }
        if (shadow.y) { radiance += contributions[slot]; }
    }
    if (!reflection_finite(radiance) || any(radiance < vec3<f32>(0)) || any(radiance > vec3<f32>(1e18))) {
        textureStore(signal, pixel, vec4<f32>(0, 0, 0, -1)); return;
    }
    textureStore(signal, pixel, vec4<f32>(radiance, hit.parameter));
    reflection_metadata(pixel, hit, u32(weight.w) - 1u);
}
