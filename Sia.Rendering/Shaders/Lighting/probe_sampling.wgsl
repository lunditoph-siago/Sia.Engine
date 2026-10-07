#define_import_path rendering/Lighting/probe_sampling

#import rendering/Lighting/probe_basis

fn rendering_sample_probe_irradiance(volume: texture_3d<f32>, volume_sampler: sampler, origin: vec3<f32>, spacing: vec3<f32>, dim: vec3<f32>, position: vec3<f32>, normal: vec3<f32>, fallback: vec3<f32>) -> vec3<f32> {
    let coordinate = (position + normal * 0.03 - origin) / spacing;
    if (any(coordinate < vec3<f32>(0)) || any(coordinate > dim - 1.0)) {
        return fallback;
    }
    var packed: array<vec4<f32>, 7>;
    for (var band = 0u; band < RENDERING_PROBE_TEXTURE_BANDS; band++) {
        let uv = (coordinate + vec3<f32>(0.5, 0.5, 0.5 + f32(band) * dim.z))
            / (dim * vec3<f32>(1, 1, f32(RENDERING_PROBE_TEXTURE_BANDS)));
        packed[band] = textureSampleLevel(volume, volume_sampler, uv, 0.0);
    }
    let validity = packed[6].w;
    if (validity < 1e-5) { return fallback; }
    let coefficients = rendering_unpack_probe(packed);
    let basis = rendering_probe_basis(normal);
    var irradiance = vec3<f32>(0);
    for (var c = 0u; c < 9u; c++) {
        irradiance += coefficients[c] * basis[c] * rendering_probe_convolution(c);
    }
    return irradiance / validity;
}
