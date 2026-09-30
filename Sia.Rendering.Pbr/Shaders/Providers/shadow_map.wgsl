#define_import_path pbr/Providers/shadow_map

#import pbr/bindings
#import rendering/Lighting/shadow_map

fn shadow_visibility(layer: u32, position: vec3<f32>) -> f32 {
    return rendering_shadow_visibility(shadow_atlas, layer, shadow_matrix(layer), position, 0.0015);
}
