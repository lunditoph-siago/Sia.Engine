#define_import_path pbr/Common/reflection_storage

// A logical vec4 occupies four byte planes. Only textureLoad is valid: filtering
// the encoded bytes would change float32 payloads and authored identity bits.
fn reflection_load_bits(image_in: texture_2d_array<f32>, pixel: vec2<i32>, layer: i32) -> vec4<u32> {
    let base = layer * 4;
    let b0 = vec4<u32>(floor(textureLoad(image_in, pixel, base, 0) * 255.0 + 0.5));
    let b1 = vec4<u32>(floor(textureLoad(image_in, pixel, base + 1, 0) * 255.0 + 0.5));
    let b2 = vec4<u32>(floor(textureLoad(image_in, pixel, base + 2, 0) * 255.0 + 0.5));
    let b3 = vec4<u32>(floor(textureLoad(image_in, pixel, base + 3, 0) * 255.0 + 0.5));
    return b0 | (b1 << vec4<u32>(8u)) | (b2 << vec4<u32>(16u)) | (b3 << vec4<u32>(24u));
}

fn reflection_load_float(image_in: texture_2d_array<f32>, pixel: vec2<i32>, layer: i32) -> vec4<f32> {
    return bitcast<vec4<f32>>(reflection_load_bits(image_in, pixel, layer));
}

fn reflection_pack_bits(value: vec4<u32>, plane: u32) -> vec4<f32> {
    let lanes = (value >> vec4<u32>(plane * 8u)) & vec4<u32>(255u);
    return vec4<f32>(lanes) / 255.0;
}

fn reflection_pack_float(value: vec4<f32>, plane: u32) -> vec4<f32> {
    return reflection_pack_bits(bitcast<vec4<u32>>(value), plane);
}
