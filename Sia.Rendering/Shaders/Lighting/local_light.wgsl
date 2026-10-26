#define_import_path rendering/Lighting/local_light

fn rendering_range_attenuation(distance: f32, range: f32) -> f32 {
    var window = 1.0;
    if (range > 0.0) {
        window = clamp(1.0 - pow(distance / range, 4.0), 0.0, 1.0);
    }
    return window * window / max(distance * distance, 1e-4);
}

fn rendering_spot_attenuation(cosine: f32, inner_cosine: f32, outer_cosine: f32) -> f32 {
    return pow(clamp((cosine - outer_cosine) / max(inner_cosine - outer_cosine, 1e-4), 0.0, 1.0), 2.0);
}
