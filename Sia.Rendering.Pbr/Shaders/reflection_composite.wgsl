#define_import_path pbr/reflection_composite

#import pbr/frame_types
#import rendering/Environment/ibl
#import pbr/Providers/reflections
#if TEMPORAL_REFLECTIONS
#import pbr/reflection_encoding
#endif

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(4) var environment: texture_cube<f32>;
@group(0) @binding(6) var environment_sampler: sampler;
@group(1) @binding(1) var normal_roughness: texture_2d<f32>;
@group(1) @binding(2) var reflection_inputs: texture_2d<f32>;
@group(1) @binding(5) var<uniform> settings: vec4<f32>;
@group(1) @binding(6) var reflection_radiance: texture_2d<f32>;

@vertex
fn reflection_vertex(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let p = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    return vec4<f32>(p * 2.0 - 1.0, 0, 1);
}

@fragment
fn reflection_composite(@builtin(position) pixel: vec4<f32>) -> @location(0) vec4<f32> {
    let coordinate = vec2<i32>(pixel.xy);
    var signal = textureLoad(reflection_radiance, coordinate, 0);
    if (signal.w == 0.0) { return vec4<f32>(0); }
#if TEMPORAL_REFLECTIONS
    signal = decode_reflection(signal);
#endif
    let input = textureLoad(reflection_inputs, coordinate, 0);
    let surface = textureLoad(normal_roughness, coordinate, 0);
    let ndc = pixel.xy / vec2<f32>(frame.size.xy) * vec2<f32>(2, -2) + vec2<f32>(-1, 1);
    let homogeneous = frame.inv_vp * vec4<f32>(ndc, input.w, 1);
    let position = homogeneous.xyz / homogeneous.w;
    let direction = reflect(normalize(position - frame.eye.xyz), normalize(surface.xyz));
    let sky = sample_environment_reflection(position, direction, surface.w);
    let confidence = clamp((settings.z - surface.w) / settings.w, 0.0, 1.0);
    // Signed replacement of the current baked/IBL term; never accumulate final HDR.
    return vec4<f32>(clamp((signal.xyz - sky) * input.xyz * confidence,
        vec3<f32>(-65504), vec3<f32>(65504)), 0);
}
