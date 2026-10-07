#define_import_path pbr/resolve

#import pbr/lighting
#import pbr/Common/reconstruction
#import pbr/Common/triangle_debug
#if SURFACE_DATA
#import rendering/Visibility/surface_data
#endif

@group(3) @binding(0) var ids: texture_2d<u32>;
@group(3) @binding(1) var depth: texture_depth_2d;
@group(3) @binding(2) var<storage, read> tile_data: array<u32>;
@group(3) @binding(3) var hdr: texture_storage_2d<rgba16float, write>;
#if SURFACE_DATA
@group(3) @binding(4) var normal_roughness: texture_storage_2d<rgba16float, write>;
@group(3) @binding(5) var base_metallic: texture_storage_2d<rgba8unorm, write>;
#endif
#if SCENE_REFLECTIONS
@group(3) @binding(7) var reflection_inputs: texture_storage_2d<rgba32float, write>;
#endif

@compute @workgroup_size(8, 8)
fn background(@builtin(global_invocation_id) id: vec3<u32>) {
    if (any(id.xy >= frame.size.xy)) {
        return;
    }
    let visible = textureLoad(ids, vec2<i32>(id.xy), 0).x;
    if (frame.size.w == 4u) {
        var color = vec3<f32>(0.02);
        if (visible != 0u) {
            color = vec3<f32>(1.0, 0.0, 1.0);
            if (valid_triangle_id(visible)) {
                color = triangle_debug_color(visible - 1u);
            }
        }
        textureStore(hdr, vec2<i32>(id.xy), vec4<f32>(color, 1.0));
        return;
    }
    if (visible != 0u) {
        return;
    }
    let ndc = (vec2<f32>(id.xy) + .5) / vec2<f32>(frame.size.xy) * vec2<f32>(2, -2) + vec2<f32>(-1, 1);
    let world = frame.inv_vp * vec4<f32>(ndc, 1, 1);
    let direction = unit(world.xyz / world.w - frame.eye.xyz);
    let color = textureSampleLevel(environment, environment_sampler, direction, 0).rgb;
    textureStore(hdr, vec2<i32>(id.xy), vec4<f32>(color, 1));
#if SURFACE_DATA
    textureStore(normal_roughness, vec2<i32>(id.xy), vec4<f32>(0.0));
    textureStore(base_metallic, vec2<i32>(id.xy), vec4<f32>(0.0));
#endif
#if SCENE_REFLECTIONS
    textureStore(reflection_inputs, vec2<i32>(id.xy), vec4<f32>(0, 0, 0, 1));
    textureStore(reflection_inputs, vec2<i32>(id.xy + vec2<u32>(frame.size.x, 0u)), vec4<f32>(0));
#endif
}

fn resolve_pixel(pixel: vec2<u32>) {
    if (any(pixel >= frame.size.xy)) {
        return;
    }
    let id = textureLoad(ids, vec2<i32>(pixel), 0).x;
    if (!valid_triangle_id(id)) {
        return;
    }
    if (materials[triangle_material(id - 1u)].indices.y != batch.x) {
        return;
    }
    let surface = surface_at(id - 1u, vec2<f32>(pixel) + .5);
#if SURFACE_DATA
    var data: VisibilitySurfaceData;
    if (surface.valid) {
        data = rendering_surface_data(
            surface.normal, surface.rough, surface.base, surface.metal);
    }
    textureStore(normal_roughness, vec2<i32>(pixel), data.normal_roughness);
    textureStore(base_metallic, vec2<i32>(pixel), data.base_metallic);
#endif
#if SCENE_REFLECTIONS
    var weight = vec3<f32>(0);
    var reflection_depth = 1.0;
    if (surface.valid) {
        let view = unit(frame.eye.xyz - surface.position);
        let brdf = sample_brdf_lut(brdf_lut, brdf_sampler, max(dot(surface.normal, view), 1e-4), surface.rough);
        weight = (mix(vec3<f32>(.04), surface.base, surface.metal) * brdf.x + vec3<f32>(brdf.y)) * surface.ao;
        // Compatibility permits neither fragment nor compute textureLoad of depth.
        // The resolved surface already lies on the rasterized triangle at this pixel.
        let clip = frame.vp * vec4<f32>(surface.position, 1);
        reflection_depth = clip.z / clip.w;
    }
    textureStore(reflection_inputs, vec2<i32>(pixel), vec4<f32>(weight, reflection_depth));
    var receiver = vec4<f32>(0);
    if (surface.valid) { receiver = vec4<f32>(surface.position, f32(receiver_instance_index(id - 1u) + 1u)); }
    textureStore(reflection_inputs, vec2<i32>(pixel + vec2<u32>(frame.size.x, 0u)), receiver);
#endif
    var color = vec3<f32>(0.0);
    if (surface.valid) {
        switch frame.size.w {
            case 1u: { color = surface.normal * .5 + .5; }
            case 2u: { color = vec3<f32>(fract(surface.uv), 0); }
            case 3u: { color = surface.base; }
            case 4u: { color = triangle_debug_color(id - 1u); }
            default: { color = shade(surface, vec2<f32>(pixel) + .5); }
        }
    }
    textureStore(hdr, vec2<i32>(pixel), vec4<f32>(min(color, vec3<f32>(65504)), 1));
}

@compute @workgroup_size(8, 8)
fn resolve_direct(@builtin(global_invocation_id) id: vec3<u32>) {
    if (frame.size.w != 4u) {
        resolve_pixel(id.xy);
    }
}

@compute @workgroup_size(8, 8)
fn resolve(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_id) local: vec3<u32>) {
    if (frame.size.w == 4u) {
        return;
    }
    let tiles = (frame.size.xy + 7u) / 8u;
    let base = batch.x * (tiles.x * tiles.y + 4u);
    let index = group.x + group.y * 65535u;
    if (index >= tile_data[base + 3u]) {
        return;
    }
    let packed = tile_data[base + 4u + index];
    resolve_pixel(vec2<u32>(packed & 65535u, packed >> 16u) * 8u + local.xy);
}
