#define_import_path rendering/Baking/environment

#import rendering/Environment/ibl

@group(0) @binding(0) var<uniform> bake_sky: Sky;
@group(0) @binding(1) var<storage, read_write> bake_data: array<vec4<f32>>;

const CUBE_TEXELS: u32 = #{ENV_CUBE_TEXELS}u;
const LUT_SIZE: u32 = #{ENV_LUT_SIZE}u;
const SAMPLES: u32 = 256u;

@compute @workgroup_size(64)
fn bake_environment(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let index = invocation.x;
    if (index >= CUBE_TEXELS + LUT_SIZE * LUT_SIZE) { return; }
    if (index >= CUBE_TEXELS) {
        let pixel = index - CUBE_TEXELS;
        let nv = (f32(pixel % LUT_SIZE) + 0.5) / f32(LUT_SIZE);
        let rough = (f32(pixel / LUT_SIZE) + 0.5) / f32(LUT_SIZE);
        let view = vec3<f32>(sqrt(1.0 - nv * nv), 0.0, nv);
        var integrated = vec2<f32>(0.0);
        for (var i = 0u; i < SAMPLES; i++) {
            let h = importance_sample_ggx(hammersley(i, SAMPLES), rough, vec3<f32>(0, 0, 1));
            let vh = max(0.0, dot(view, h));
            let light = 2.0 * vh * h - view;
            let nl = max(0.0, light.z);
            if (nl == 0.0) { continue; }
            let k = rough * rough * 0.5;
            let g = nv / (nv * (1.0 - k) + k) * nl / (nl * (1.0 - k) + k);
            let visibility = g * vh / max(h.z * nv, 1e-4);
            let f = pow(1.0 - vh, 5.0);
            integrated += vec2<f32>(1.0 - f, f) * visibility;
        }
        bake_data[index] = vec4<f32>(integrated / f32(SAMPLES), 0, 1);
        return;
    }
    var local = index;
    var size = #{ENV_CUBE_SIZE}u;
    var mip = 0u;
    loop {
        let count = size * size * 6u;
        if (local < count) { break; }
        local -= count; size /= 2u; mip++;
    }
    let face = local / (size * size);
    let pixel = local % (size * size);
    let uv = (vec2<f32>(f32(pixel % size), f32(pixel / size)) + 0.5) / f32(size) * 2.0 - 1.0;
    let normal = cube_face_direction(face, uv);
    let rough = f32(mip) / f32(#{ENV_MIP_COUNT}u - 1u);
    let count = select(SAMPLES, 1u, mip == 0u);
    var color = vec3<f32>(0.0);
    var weight = 0.0;
    for (var i = 0u; i < count; i++) {
        let h = importance_sample_ggx(hammersley(i, SAMPLES), rough, normal);
        let light = 2.0 * dot(normal, h) * h - normal;
        let nl = max(dot(normal, light), 0.0);
        color += sky_color(light, bake_sky) * nl; weight += nl;
    }
    bake_data[index] = vec4<f32>(min(color / max(weight, 1e-6), vec3<f32>(65504)), 1);
}

@compute @workgroup_size(1)
fn bake_sh(@builtin(global_invocation_id) invocation: vec3<u32>) {
    let coefficient = invocation.x;
    if (coefficient >= 9u) { return; }
    var sum = vec3<f32>(0.0);
    for (var i = 0u; i < 4096u; i++) {
        let y = 1.0 - 2.0 * (f32(i) + 0.5) / 4096.0;
        let radius = sqrt(max(0.0, 1.0 - y * y));
        let theta = 2.399963229728653 * f32(i);
        let d = vec3<f32>(cos(theta) * radius, y, sin(theta) * radius);
        let basis = array<f32, 9>(IBL_SH_Y00, IBL_SH_Y1 * d.y, IBL_SH_Y1 * d.z, IBL_SH_Y1 * d.x,
            IBL_SH_Y2MN * d.x * d.y, IBL_SH_Y2MN * d.y * d.z, IBL_SH_Y20 * (3.0 * d.z * d.z - 1.0),
            IBL_SH_Y2MN * d.x * d.z, IBL_SH_Y22 * (d.x * d.x - d.y * d.y));
        sum += sky_color(d, bake_sky) * basis[coefficient];
    }
    bake_data[CUBE_TEXELS + LUT_SIZE * LUT_SIZE + coefficient] = vec4<f32>(sum * (4.0 * IBL_PI / 4096.0), 0);
}
