#define_import_path rendering/Lighting/probe_basis

const RENDERING_PROBE_TEXTURE_BANDS: u32 = 7u;

// Flatten nine RGB coefficients, then append the interpolation validity.
fn rendering_pack_probe(c: array<vec3<f32>, 9>, validity: f32) -> array<vec4<f32>, 7> {
    return array<vec4<f32>, 7>(
        vec4<f32>(c[0], c[1].x),
        vec4<f32>(c[1].yz, c[2].xy),
        vec4<f32>(c[2].z, c[3]),
        vec4<f32>(c[4], c[5].x),
        vec4<f32>(c[5].yz, c[6].xy),
        vec4<f32>(c[6].z, c[7]),
        vec4<f32>(c[8], validity));
}

fn rendering_unpack_probe(p: array<vec4<f32>, 7>) -> array<vec3<f32>, 9> {
    return array<vec3<f32>, 9>(
        p[0].xyz, vec3<f32>(p[0].w, p[1].xy),
        vec3<f32>(p[1].zw, p[2].x), p[2].yzw,
        p[3].xyz, vec3<f32>(p[3].w, p[4].xy),
        vec3<f32>(p[4].zw, p[5].x), p[5].yzw, p[6].xyz);
}

fn rendering_probe_basis(d: vec3<f32>) -> array<f32, 9> {
    return array<f32, 9>(0.2820948, 0.4886025*d.y, 0.4886025*d.z, 0.4886025*d.x,
        1.0925484*d.x*d.y, 1.0925484*d.y*d.z, 0.3153916*(3.0*d.z*d.z-1.0),
        1.0925484*d.x*d.z, 0.5462742*(d.x*d.x-d.y*d.y));
}

fn rendering_probe_convolution(coefficient: u32) -> f32 {
    return select(select(0.78539816, 2.0943951, coefficient < 4u), 3.14159265, coefficient == 0u);
}
