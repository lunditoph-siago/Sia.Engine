#define_import_path pbr/Providers/environment

#import pbr/bindings
#import pbr/pbr_lighting
#import pbr/Common/surface
#if STATIC_LIGHTMAP
#import pbr/Providers/lightmap
#endif
#if SCENE_GI
#import rendering/Lighting/probe_basis
#endif

#if SCENE_GI
fn scene_probe_irradiance(position: vec3<f32>, normal: vec3<f32>, correction: bool) -> vec3<f32> {
    let paired = probe_header[2].z > 0.5;
    let dim = vec3<f32>(probe_header[0].w, probe_header[1].w, probe_header[2].x);
    let coordinate = (position + normal * 0.03 - probe_header[0].xyz) / probe_header[1].xyz;
    if (any(coordinate < vec3<f32>(0)) || any(coordinate > dim - 1.0)) {
        if (correction) { return vec3<f32>(0.0); }
        return evaluate_sh_irradiance(sh, normal);
    }
    var packed: array<vec4<f32>, 7>;
    let texture_bands = RENDERING_PROBE_TEXTURE_BANDS * select(1u, 2u, paired);
    let first_band = select(0u, RENDERING_PROBE_TEXTURE_BANDS, paired && !correction);
    for (var band = 0u; band < RENDERING_PROBE_TEXTURE_BANDS; band++) {
        let uv = (coordinate + vec3<f32>(0.5, 0.5, 0.5 + f32(band + first_band) * dim.z))
            / (dim * vec3<f32>(1, 1, f32(texture_bands)));
        packed[band] = textureSampleLevel(probe_volume, environment_sampler, uv, 0.0);
    }
    let validity = packed[6].w;
    if (validity < 1e-5) {
        if (correction) { return vec3<f32>(0.0); }
        return evaluate_sh_irradiance(sh, normal);
    }
    let coefficients = rendering_unpack_probe(packed);
    let basis = rendering_probe_basis(normal);
    var irradiance = vec3<f32>(0);
    for (var c = 0u; c < 9u; c++) {
        irradiance += coefficients[c] * basis[c] * rendering_probe_convolution(c);
    }
    // Unknown pairs carry zero weight; do not amplify a sparse valid corner into full correction.
    if (correction) { return irradiance; }
    return max(irradiance / validity, vec3<f32>(0));
}
#endif

fn get_indirect_lighting(surface: Surface, view: vec3<f32>) -> vec3<f32> {
    let position = surface.position; let normal = surface.normal;
    let base = surface.base; let metal = surface.metal; let rough = surface.rough;
    var irradiance = vec3<f32>(0);
    // Pure reflection and fully metallic surfaces have no diffuse contribution.
    if (metal < 1.0 && any(base != vec3<f32>(0))) {
#if STATIC_LIGHTMAP
        let baked = lightmap_irradiance(surface);
        if (baked.w > 0.0) {
            irradiance = baked.xyz;
#if SCENE_GI
            if (probe_header[2].z > 0.5) { irradiance += scene_probe_irradiance(position, normal, true); }
#endif
        }
        else {
#endif
#if SCENE_GI
        irradiance = scene_probe_irradiance(position, normal, false);
#else
        irradiance = evaluate_sh_irradiance(sh, normal);
#endif
#if STATIC_LIGHTMAP
        }
#endif
    }
    return indirect_lighting(normal, view, base, metal, rough, max(irradiance, vec3<f32>(0.0)),
        environment, environment_sampler, f32(textureNumLevels(environment)), brdf_lut, brdf_sampler);
}
