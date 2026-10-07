#define_import_path pbr/reflections

#import pbr/frame_types
#import rendering/Environment/ibl
#import rendering/Lighting/scene_trace
#import rendering/Lighting/probe_sampling
#import rendering/Lighting/local_light

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<storage, read> scene_data: array<vec4<f32>>;
@group(0) @binding(4) var environment: texture_cube<f32>;
@group(0) @binding(6) var environment_sampler: sampler;
@group(0) @binding(8) var<uniform> sh: array<vec4<f32>, 9>;
@group(0) @binding(9) var probe_volume: texture_3d<f32>;
@group(0) @binding(10) var<uniform> probe_header: array<vec4<f32>, 3>;
@group(1) @binding(1) var normal_roughness: texture_2d<f32>;
@group(1) @binding(2) var reflection_inputs: texture_2d<f32>;
@group(1) @binding(3) var<storage, read> tracing: array<vec4<f32>>;
#if DYNAMIC_TRACE
@group(1) @binding(4) var<storage, read> dynamic_tracing: array<vec4<f32>>;
#endif
// Maximum ray distance and node visits per BVH, roughness cutoff and fade width.
@group(1) @binding(5) var<uniform> settings: vec4<f32>;

fn trace_record(index: u32, dynamic: bool) -> vec4<f32> {
#if DYNAMIC_TRACE
    if (dynamic) { return dynamic_tracing[index]; }
#endif
    return tracing[index];
}

@vertex
fn reflection_vertex(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let p = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    return vec4<f32>(p * 2.0 - 1.0, 0, 1);
}

@fragment
fn reflection_fragment(@builtin(position) pixel: vec4<f32>) -> @location(0) vec4<f32> {
    let coordinate = vec2<i32>(pixel.xy);
    let input = textureLoad(reflection_inputs, coordinate, 0);
    let z = input.w;
    let surface = textureLoad(normal_roughness, coordinate, 0);
    let weight = input.xyz;
    if (z >= 1.0 || surface.w >= settings.z || dot(surface.xyz, surface.xyz) < .5 || all(weight == vec3<f32>(0))) {
        return vec4<f32>(0);
    }
    let ndc = pixel.xy / vec2<f32>(frame.size.xy) * vec2<f32>(2, -2) + vec2<f32>(-1, 1);
    let homogeneous = frame.inv_vp * vec4<f32>(ndc, z, 1);
    let position = homogeneous.xyz / homogeneous.w;
    let normal = normalize(surface.xyz);
    let direction = reflect(normalize(position - frame.eye.xyz), normal);
    let hit = trace_scene(position + normal * .01, direction, settings.x, true, u32(settings.y));
    // Partial traversal cannot prove the nearest hit. Keep the original IBL.
    if (!hit.complete || hit.triangle == 0xffffffffu) { return vec4<f32>(0); }
    let indices = vec4<u32>(trace_record(u32(trace_record(0u, hit.dynamic).y) + hit.triangle, hit.dynamic));
    let vertex_start = u32(trace_record(1u, hit.dynamic).x);
    let a = trace_record(vertex_start + indices.x, hit.dynamic).xyz;
    let b = trace_record(vertex_start + indices.y, hit.dynamic).xyz;
    let c = trace_record(vertex_start + indices.z, hit.dynamic).xyz;
    let material = u32(trace_record(0u, hit.dynamic).w) + indices.w * 2u;
    let albedo = trace_record(material, hit.dynamic);
    if (!hit.front && albedo.w < .5) { return vec4<f32>(0); }
    var hit_normal = normalize(cross(b - a, c - a));
    if (!hit.front) { hit_normal = -hit_normal; }
    let point = position + normal * .01 + direction * hit.distance + hit_normal * .01;
    // Coarse diffuse material cache; textured/specular hit shading follows separately.
    var radiance = trace_record(material + 1u, hit.dynamic).xyz;
    let irradiance = rendering_sample_probe_irradiance(probe_volume, environment_sampler,
        probe_header[0].xyz, probe_header[1].xyz, vec3<f32>(probe_header[0].w, probe_header[1].w, probe_header[2].x),
        point, hit_normal, evaluate_sh_irradiance(sh, hit_normal));
    radiance += albedo.xyz * max(irradiance, vec3<f32>(0)) / IBL_PI;
    for (var i = 0u; i < frame.counts.y; i++) {
        let to_light = -frame.directional[i].direction.xyz;
        let cosine = max(dot(hit_normal, to_light), 0.0);
        if (cosine <= 0.0) { continue; }
        let shadow = trace_scene(point, to_light, settings.x, true, u32(settings.y));
        if (!shadow.complete) { return vec4<f32>(0); }
        if (shadow.triangle == 0xffffffffu) {
            radiance += albedo.xyz * frame.directional[i].radiance.xyz * cosine / IBL_PI;
        }
    }
    // Reflected hits can be offscreen; use the complete bounded view light table,
    // rather than the primary surface's camera-space light cluster.
    for (var i = 0u; i < frame.counts.x; i++) {
        let light = scene_data[i * 4u];
        let delta = light.xyz - point;
        let distance = length(delta);
        if (distance >= light.w) { continue; }
        let to_light = delta / max(distance, 1e-4);
        let cosine = max(dot(hit_normal, to_light), 0.0);
        if (cosine <= 0.0) { continue; }
        var attenuation = rendering_range_attenuation(distance, light.w);
        let direction = scene_data[i * 4u + 1u];
        if (direction.w > .5) {
            let spot = scene_data[i * 4u + 3u];
            attenuation *= rendering_spot_attenuation(dot(-to_light, direction.xyz), spot.x, spot.y);
        }
        if (attenuation <= 0.0) { continue; }
        let shadow = trace_scene(point, to_light, max(distance - .01, .001), true, u32(settings.y));
        if (!shadow.complete) { return vec4<f32>(0); }
        if (shadow.triangle == 0xffffffffu) {
            radiance += albedo.xyz * scene_data[i * 4u + 2u].xyz * (attenuation * cosine / IBL_PI);
        }
    }
    // Independent scene radiance for future temporal filtering. Black hits remain valid.
    // Current BRDF/AO, confidence and baked baseline belong to composition, not history.
    return vec4<f32>(radiance, max(hit.distance, .0001));
}
