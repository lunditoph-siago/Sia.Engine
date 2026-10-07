#define_import_path pbr/reflection_encoding

// Half-float logarithms retain HDR radiance and hit distance. Zero alpha is invalid.
fn encode_reflection(value: vec4<f32>) -> vec4<f32> {
    return log2(vec4<f32>(1) + vec4<f32>(max(value.xyz, vec3<f32>(0)), max(value.w, .0001)));
}

fn decode_reflection(value: vec4<f32>) -> vec4<f32> {
    return exp2(min(value, vec4<f32>(127))) - vec4<f32>(1);
}
