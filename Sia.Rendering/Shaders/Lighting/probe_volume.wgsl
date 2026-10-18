#define_import_path rendering/Lighting/probe_volume

#import rendering/Lighting/probe_basis
#import rendering/Environment/ibl

fn rendering_scene_probe_irradiance(volume: texture_3d<f32>, volume_sampler: sampler,
    header: array<vec4<f32>, 3>, coefficients_sh: array<vec4<f32>, 9>,
    position: vec3<f32>, normal: vec3<f32>, correction: bool) -> vec3<f32> {
    let paired = header[2].z > 0.5;
    let dim = vec3<f32>(header[0].w, header[1].w, header[2].x);
    let coordinate = (position + normal * 0.03 - header[0].xyz) / header[1].xyz;
    if (any(coordinate < vec3<f32>(0)) || any(coordinate > dim - 1.0)) {
        if (correction) { return vec3<f32>(0.0); }
        return evaluate_sh_irradiance(coefficients_sh, normal);
    }
    var packed: array<vec4<f32>, 7>;
    let bands = RENDERING_PROBE_TEXTURE_BANDS * select(1u, 2u, paired);
    let first = select(0u, RENDERING_PROBE_TEXTURE_BANDS, paired && !correction);
    for (var band = 0u; band < RENDERING_PROBE_TEXTURE_BANDS; band++) {
        let uv = (coordinate + vec3<f32>(0.5, 0.5, 0.5 + f32(band + first) * dim.z)) / (dim * vec3<f32>(1, 1, f32(bands)));
        packed[band] = textureSampleLevel(volume, volume_sampler, uv, 0.0);
    }
    let validity = packed[6].w;
    if (validity < 1e-5) {
        if (correction) { return vec3<f32>(0.0); }
        return evaluate_sh_irradiance(coefficients_sh, normal);
    }
    let coefficients = rendering_unpack_probe(packed); let basis = rendering_probe_basis(normal);
    var irradiance = vec3<f32>(0);
    for (var c = 0u; c < 9u; c++) { irradiance += coefficients[c] * basis[c] * rendering_probe_convolution(c); }
    // Sparse unknown pairs retain zero weight; absolute live values normalize valid samples.
    if (correction) { return irradiance; }
    return max(irradiance / validity, vec3<f32>(0));
}
