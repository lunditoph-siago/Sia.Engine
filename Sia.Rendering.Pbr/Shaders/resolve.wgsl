#define_import_path pbr/resolve

#import pbr/lighting
#import pbr/Common/reconstruction
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

@compute @workgroup_size(8, 8)
fn background(@builtin(global_invocation_id) id: vec3<u32>) {
    if (any(id.xy >= frame.size.xy)) {
        return;
    }
    if (textureLoad(ids, vec2<i32>(id.xy), 0).x != 0u) {
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
}

@compute @workgroup_size(8, 8)
fn resolve(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_id) local: vec3<u32>) {
    let tiles = (frame.size.xy + 7u) / 8u;
    let base = batch.x * (tiles.x * tiles.y + 4u);
    let index = group.x + group.y * 65535u;
    if (index >= tile_data[base + 3u]) {
        return;
    }
    let packed = tile_data[base + 4u + index];
    let pixel = vec2<u32>(packed & 65535u, packed >> 16u) * 8u + local.xy;
    if (any(pixel >= frame.size.xy)) {
        return;
    }
    let id = textureLoad(ids, vec2<i32>(pixel), 0).x;
    if (id == 0u || id > frame.geometry.z) {
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
    var color = vec3<f32>(0.0);
    if (surface.valid) {
        color = shade(surface, vec2<f32>(pixel) + .5);
        if (frame.size.w == 1u) {
            color = surface.normal * .5 + .5;
        }
        if (frame.size.w == 2u) {
            color = vec3<f32>(fract(surface.uv), 0);
        }
        if (frame.size.w == 3u) {
            color = surface.base;
        }
        if (frame.size.w == 4u) {
            let hash = ((id - 1u) + 1u) * 2654435761u;
            color = vec3<f32>(f32(hash & 255u), f32((hash >> 8u) & 255u), f32(hash >> 24u)) / 255.0;
        }
    }
    textureStore(hdr, vec2<i32>(pixel), vec4<f32>(min(color, vec3<f32>(65504)), 1));
}
