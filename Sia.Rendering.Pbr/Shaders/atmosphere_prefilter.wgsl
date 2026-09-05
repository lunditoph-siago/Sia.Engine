#import pbr::atmosphere::{sky_radiance}
#import pbr::ibl::{ibl_fullscreen_ndc, hammersley, importance_sample_ggx, cube_face_direction, Sky}

struct Prefilter { params: vec4<f32>, sky: Sky, };
@group(1) @binding(0) var<uniform> prefilter: Prefilter;

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) ndc: vec2<f32>,
};

@vertex
fn vertex(@builtin(vertex_index) index: u32) -> VertexOutput {
    let ndc = ibl_fullscreen_ndc(index);
    return VertexOutput(vec4<f32>(ndc, 0.0, 1.0), ndc);
}

@fragment
fn fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    let n = cube_face_direction(u32(prefilter.params.z), input.ndc * vec2<f32>(1.0, -1.0));
    let count = u32(prefilter.params.y);
    var radiance = vec3<f32>(0.0);
    var weight = 0.0;
    for (var i = 0u; i < count; i += 1u) {
        let h = importance_sample_ggx(hammersley(i, count), prefilter.params.x, n);
        let l = normalize(reflect(-n, h));
        let no_l = max(dot(n, l), 0.0);
        if (no_l > 0.0) { radiance += sky_radiance(l) * no_l; weight += no_l; }
    }
    return vec4<f32>(min(radiance / max(weight, 1e-6), vec3<f32>(65504.0)), 1.0);
}
