#define_import_path pbr/output

#import rendering/Common/color
#if DISPLAY_TRANSFORM
#import pbr/Providers/display
#endif

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

fn output_hdr(uv: vec2<f32>) -> vec3<f32> {
    let size = vec2<i32>(textureDimensions(source_hdr));
    let pixel = uv * vec2<f32>(size) - .5;
    let first = vec2<i32>(floor(pixel));
    let weight = fract(pixel);
    let last = size - vec2<i32>(1);
    let a = textureLoad(source_hdr, clamp(first, vec2<i32>(0), last), 0).rgb;
    // At exact texel centers the common equal-size path needs only one load.
    if (all(weight == vec2<f32>(0))) { return a; }
    let b = textureLoad(source_hdr, clamp(first + vec2<i32>(1, 0), vec2<i32>(0), last), 0).rgb;
    let c = textureLoad(source_hdr, clamp(first + vec2<i32>(0, 1), vec2<i32>(0), last), 0).rgb;
    let d = textureLoad(source_hdr, clamp(first + vec2<i32>(1), vec2<i32>(0), last), 0).rgb;
    return mix(mix(a, b, weight.x), mix(c, d, weight.x), weight.y);
}

@fragment
fn output_fragment(input: OutputVertex) -> @location(0) vec4<f32> {
#if DISPLAY_TRANSFORM
    let limit = 3.402823e38 / max(output_settings.x, 1.0);
    let exposed = clamp(output_hdr(input.uv), vec3<f32>(-limit), vec3<f32>(limit)) * output_settings.x;
    var color = rendering_display_transform(exposed);
    // Authored programs already return display-encoded sRGB. An sRGB attachment
    // applies its hardware OETF, so cancel that one encoding before the store.
    if (output_settings.y < 0.5) { color = rendering_srgb_to_linear(color); }
#else
    let exposed = rendering_exposure(output_hdr(input.uv), output_settings.x);
    var color = rendering_aces_fitted(exposed);
    if (output_settings.y > 0.5) {
        color = rendering_linear_to_srgb(color);
    }
#endif
    return vec4<f32>(color, 1);
}
