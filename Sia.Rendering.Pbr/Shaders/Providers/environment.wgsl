#define_import_path pbr/Providers/environment

#import pbr/bindings
#import pbr/pbr_lighting
#import pbr/Providers/reflections
#if SCENE_GI
#import rendering/Lighting/probe_basis
#endif

#if SCENE_GI
#import rendering/Lighting/probe_sampling

fn sample_probe_irradiance(volume: texture_3d<f32>, position: vec3<f32>, normal: vec3<f32>, fallback: vec3<f32>) -> vec3<f32> {
    return rendering_sample_probe_irradiance(volume, environment_sampler, probe_header[0].xyz, probe_header[1].xyz,
        vec3<f32>(probe_header[0].w, probe_header[1].w, probe_header[2].x), position, normal, fallback);
}

fn scene_probe_irradiance(position: vec3<f32>, normal: vec3<f32>) -> vec3<f32> {
    return max(sample_probe_irradiance(probe_volume, position, normal, evaluate_sh_irradiance(sh, normal)), vec3<f32>(0));
}
#endif

#if PAGED_LIGHTMAPS
fn unpack_lightmap_bytes(value: u32) -> vec4<f32> {
    return vec4<f32>(f32(value & 255u), f32((value >> 8u) & 255u), f32((value >> 16u) & 255u), f32(value >> 24u)) / 255.0;
}

fn paged_lightmap_level(uv: vec2<f32>, chart_index: u32, chart: vec4<f32>, mip: u32) -> mat4x4<f32> {
    let base = lightmap_metadata_base();
    let header = geometry_record(base);
    let coarse_base = base + 1u + u32(header.x) * 4u + u32(header.y);
    let receiver_base = coarse_base + u32(header.y);
    let receiver = geometry_record(receiver_base + u32(chart.w));
    let origin = bitcast<u32>(receiver.x);
    let pixel = (uv * header.w - vec2<f32>(f32(origin & 65535u), f32(origin >> 16u))) / exp2(f32(mip));
    let page_xy = vec2<u32>(floor(max(pixel, vec2<f32>(0)) / 32.0));
    var address = u32(receiver.z);
    for (var level = 0u; level < mip; level++) {
        let side = (u32(receiver.y) >> level) + 31u;
        address += (side / 32u) * (side / 32u);
    }
    let side = ((u32(receiver.y) >> mip) + 31u) / 32u;
    address += page_xy.y * side + page_xy.x;
    let table_base = receiver_base + u32(header.x);
    let mapped = bitcast<vec4<u32>>(geometry_record(table_base + address / 4u))[address % 4u];
    if (mapped == 0u) {
        let coarse = bitcast<vec4<u32>>(geometry_record(coarse_base + chart_index));
        return mat4x4<f32>(unpack_lightmap_bytes(coarse.x), unpack_lightmap_bytes(coarse.y),
            unpack_lightmap_bytes(coarse.z), unpack_lightmap_bytes(coarse.w));
    }
    let dimensions = textureDimensions(lightmap_atlas);
    let grid = dimensions.x / 34u;
    let slot = mapped - 1u;
    let physical = vec2<f32>(f32(slot % grid), f32(slot / grid)) * 34.0
        + pixel - vec2<f32>(page_xy) * 32.0 + 1.0;
    let coordinate = physical / vec2<f32>(dimensions);
    return mat4x4<f32>(textureSampleLevel(lightmap_atlas, environment_sampler, coordinate, 0, 0.0),
        textureSampleLevel(lightmap_atlas, environment_sampler, coordinate, 1, 0.0),
        textureSampleLevel(lightmap_atlas, environment_sampler, coordinate, 2, 0.0),
        textureSampleLevel(lightmap_atlas, environment_sampler, coordinate, 3, 0.0));
}
#endif

fn get_indirect_lighting(
    position: vec3<f32>,
    normal: vec3<f32>,
    view: vec3<f32>,
    base: vec3<f32>,
    metal: f32,
    rough: f32,
    lightmap: vec3<f32>,
    lightmap_dx: vec2<f32>,
    lightmap_dy: vec2<f32>,
) -> vec3<f32> {
    var irradiance = vec3<f32>(0);
    // Pure reflection and fully metallic surfaces have no diffuse contribution.
    if (metal < 1.0 && any(base != vec3<f32>(0))) {
        var has_surface_irradiance = false;
#if LIGHTMAPS
        if (lightmap.z > 0.5) {
            var uv = lightmap.xy;
            var lod = 0.0;
#if LIGHTMAP_MIPS
            let metadata_base = lightmap_metadata_base();
            let header = geometry_record(metadata_base);
            let chart = geometry_record(metadata_base + 1u + u32(header.x) * 4u + u32(round(lightmap.z)) - 1u);
            let origin = bitcast<u32>(chart.x);
            let extent = bitcast<u32>(chart.y);
            let chart_min = vec2<f32>(f32(origin & 65535u), f32(origin >> 16u));
            let chart_max = chart_min + vec2<f32>(f32(extent & 65535u), f32(extent >> 16u));
#if PAGED_LIGHTMAPS
            let dimensions = vec2<f32>(header.w);
#else
            let dimensions = vec2<f32>(textureDimensions(lightmap_atlas));
#endif
            let dx = lightmap_dx * dimensions;
            let dy = lightmap_dy * dimensions;
            lod = min(chart.z, 0.5 * log2(max(1.0, max(dot(dx, dx), dot(dy, dy)))));
            let inset = vec2<f32>(0.5 * exp2(ceil(lod)));
            uv = clamp(uv, (chart_min + inset) / dimensions, (chart_max - inset) / dimensions);
#endif
#if PAGED_LIGHTMAPS
            let chart_index = u32(round(lightmap.z)) - 1u;
            let lower = paged_lightmap_level(uv, chart_index, chart, u32(floor(lod)));
            let upper = paged_lightmap_level(uv, chart_index, chart, u32(ceil(lod)));
            let weight = fract(lod);
            let sampled = mat4x4<f32>(mix(lower[0], upper[0], weight), mix(lower[1], upper[1], weight),
                mix(lower[2], upper[2], weight), mix(lower[3], upper[3], weight));
            let c0 = sampled[0];
#else
            let c0 = textureSampleLevel(lightmap_atlas, environment_sampler, uv, 0, lod);
#endif
            if (c0.w > 1e-5) {
                var constant = c0.xyz;
#if PAGED_LIGHTMAPS
                var cy = sampled[1].xyz;
                var cz = sampled[2].xyz;
                var cx = sampled[3].xyz;
#else
                var cy = textureSampleLevel(lightmap_atlas, environment_sampler, uv, 1, lod).xyz;
                var cz = textureSampleLevel(lightmap_atlas, environment_sampler, uv, 2, lod).xyz;
                var cx = textureSampleLevel(lightmap_atlas, environment_sampler, uv, 3, lod).xyz;
#endif
#if QUANTIZED_LIGHTMAPS
#if LIGHTMAP_MIPS
                let scale_base = metadata_base + 1u + u32(chart.w) * 4u;
#else
                let receiver = u32(round(lightmap.z)) - 1u;
                let scale_base = lightmap_metadata_base() + receiver * 4u;
#endif
                constant *= geometry_record(scale_base).xyz;
                // Invalid texels are zero. Remove the signed zero point weighted
                // by interpolated coverage before normalizing the same field.
                let zero = vec3<f32>((128.0 / 255.0) * c0.w);
                cy = (cy - zero) * (255.0 / 127.0) * geometry_record(scale_base + 1u).xyz;
                cz = (cz - zero) * (255.0 / 127.0) * geometry_record(scale_base + 2u).xyz;
                cx = (cx - zero) * (255.0 / 127.0) * geometry_record(scale_base + 3u).xyz;
#endif
                // Replace the same diffuse field, including a valid black bake. Direct
                // lights, emission, material response and specular remain separate.
                irradiance = max((constant * (0.2820948 * 3.14159265)
                    + (cy * normal.y + cz * normal.z + cx * normal.x) * (0.4886025 * 2.0943951)) / c0.w, vec3<f32>(0));
                has_surface_irradiance = true;
#if LIGHTMAP_SCENE_GI
                // The difference is signed. Missing/invalid pairs contribute zero;
                // clamp after addition so live occluders can darken a valid bake.
                irradiance = max(irradiance + sample_probe_irradiance(probe_difference, position, normal, vec3<f32>(0)), vec3<f32>(0));
#endif
            }
        }
#endif
        if (!has_surface_irradiance) {
#if SCENE_GI
            irradiance = scene_probe_irradiance(position, normal);
#else
            irradiance = evaluate_sh_irradiance(sh, normal);
#endif
        }
    }
    return indirect_lighting(normal, view, base, metal, rough, irradiance,
        sample_environment_reflection(position, reflect(-view, normal), rough), brdf_lut, brdf_sampler);
}
