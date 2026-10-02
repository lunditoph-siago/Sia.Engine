#define_import_path pbr/output

#import rendering/Common/color

@group(0) @binding(0) var<uniform> output_settings: vec4<f32>;
@group(0) @binding(1) var source_hdr: texture_2d<f32>;
@group(0) @binding(2) var source_sampler: sampler;

struct OutputVertex {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>
}

@vertex
fn output_vertex(@builtin(vertex_index) index: u32) -> OutputVertex {
    let p = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    return OutputVertex(vec4<f32>(p * 2.0 - 1.0, 0, 1), p * vec2<f32>(1, -1) + vec2<f32>(0, 1));
}

@fragment
fn output_fragment(input: OutputVertex) -> @location(0) vec4<f32> {
    let exposed = rendering_exposure(textureSampleLevel(source_hdr, source_sampler, input.uv, 0).rgb, output_settings.x);
    var color = rendering_aces_fitted(exposed);
    if (output_settings.y > 0.5) {
        color = rendering_linear_to_srgb(color);
    }
    return vec4<f32>(color, 1);
}
