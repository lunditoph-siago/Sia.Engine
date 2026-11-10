#define_import_path pbr/Common/hdr

// Float32 attachments retain bright transparent layers until fixed blending
// applies their alpha. Half admission is bounded before encoding any frame.
fn pbr_store_hdr(radiance: vec3<f32>, inverse_scale: f32) -> vec3<f32> {
#if FULL_PRECISION_HDR
    return radiance * inverse_scale;
#else
    return min(radiance * inverse_scale, vec3<f32>(65504));
#endif
}
