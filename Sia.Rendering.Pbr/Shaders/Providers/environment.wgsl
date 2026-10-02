#define_import_path pbr/Providers/environment

#import pbr/bindings
#import pbr/pbr_lighting
#if SCENE_GI
#import rendering/Lighting/probe_basis
#endif

#if SCENE_GI
fn scene_probe_irradiance(position: vec3<f32>, normal: vec3<f32>) -> vec3<f32> {
    let dim = vec3<f32>(probe_header[0].w, probe_header[1].w, probe_header[2].x);
    let coordinate = (position + normal * 0.03 - probe_header[0].xyz) / probe_header[1].xyz;
    if (any(coordinate < vec3<f32>(0)) || any(coordinate > dim - 1.0)) {
        return evaluate_sh_irradiance(sh, normal);
    }
    var packed: array<vec4<f32>, 7>;
    for (var band = 0u; band < RENDERING_PROBE_TEXTURE_BANDS; band++) {
        let uv = (coordinate + vec3<f32>(0.5, 0.5, 0.5 + f32(band) * dim.z))
            / (dim * vec3<f32>(1, 1, f32(RENDERING_PROBE_TEXTURE_BANDS)));
        packed[band] = textureSampleLevel(probe_volume, environment_sampler, uv, 0.0);
    }
    let validity = packed[6].w;
    if (validity < 1e-5) { return evaluate_sh_irradiance(sh, normal); }
    let coefficients = rendering_unpack_probe(packed);
    let basis = rendering_probe_basis(normal);
    var irradiance = vec3<f32>(0);
    for (var c = 0u; c < 9u; c++) {
        irradiance += coefficients[c] * basis[c] * rendering_probe_convolution(c);
    }
    return max(irradiance / validity, vec3<f32>(0));
}
#endif

fn get_indirect_lighting(
    position: vec3<f32>,
    normal: vec3<f32>,
    view: vec3<f32>,
    base: vec3<f32>,
    metal: f32,
    rough: f32,
) -> vec3<f32> {
    var irradiance = vec3<f32>(0);
    // Pure reflection and fully metallic surfaces have no diffuse contribution.
    if (metal < 1.0 && any(base != vec3<f32>(0))) {
#if SCENE_GI
        irradiance = scene_probe_irradiance(position, normal);
#else
        irradiance = evaluate_sh_irradiance(sh, normal);
#endif
    }
    return indirect_lighting(normal, view, base, metal, rough, irradiance,
        environment, environment_sampler, f32(textureNumLevels(environment)), brdf_lut, brdf_sampler);
}
