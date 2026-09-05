#import pbr::ibl::{ibl_fullscreen_ndc}

@group(0) @binding(0) var<uniform> inverse_view_projection: mat4x4<f32>;
@group(0) @binding(1) var environment: texture_cube<f32>;
@group(0) @binding(2) var environment_sampler: sampler;

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) ndc: vec2<f32>,
};

@vertex
fn vertex(@builtin(vertex_index) index: u32) -> VertexOutput {
    let ndc = ibl_fullscreen_ndc(index);
    var output: VertexOutput;
    output.position = vec4<f32>(ndc, 0.0, 1.0);
    output.ndc = ndc;
    return output;
}

@fragment
fn fragment(input: VertexOutput) -> @location(0) vec4<f32> {
    let near = inverse_view_projection * vec4<f32>(input.ndc, 0.0, 1.0);
    let far = inverse_view_projection * vec4<f32>(input.ndc, 0.5, 1.0);
    let direction = normalize(far.xyz / far.w - near.xyz / near.w);
    return vec4<f32>(textureSampleLevel(environment, environment_sampler, direction, 0.0).rgb, 1.0);
}
