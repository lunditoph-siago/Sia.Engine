#define_import_path pbr/lighting

#import pbr/Common/surface
#import pbr/Providers/shadow_map
#import pbr/Providers/environment
#import rendering/Lighting/local_light

fn scene_lighting(
    position: vec3<f32>,
    normal: vec3<f32>,
    base: vec3<f32>,
    metal: f32,
    rough: f32,
    emission: vec3<f32>,
    ao: f32,
    pixel: vec2<f32>,
    lightmap: vec3<f32>,
    lightmap_dx: vec2<f32>,
    lightmap_dy: vec2<f32>,
) -> vec3<f32> {
    let view = unit(frame.eye.xyz - position);
    let view_z = (frame.view * vec4<f32>(position, 1.0)).z;
    var color = vec3<f32>(0.0);
    for (var i = 0u; i < frame.counts.y; i++) {
        if (dot(normal, -frame.directional[i].direction.xyz) <= 0.0) { continue; }
        var visibility = 1.0;
        if (i == frame.counts.z && frame.shadow.x != 0u) {
            var layer = 0u;
            if (-view_z > frame.splits.x) {
                layer = 1u;
            }
            if (-view_z > frame.splits.y) {
                layer = 2u;
            }
            layer = min(layer, frame.shadow.x - 1u);
            visibility = shadow_visibility(layer, position);
        }
        color += visibility * direct_lighting(normal, view, -frame.directional[i].direction.xyz, frame.directional[i].radiance.rgb, base, metal, rough);
    }
    let slice = min(u32(max(0.0, log(max(-view_z, 0.00001)) * frame.depth.x - frame.depth.y)), frame.grid.z - 1u);
    let tile = min(vec2<u32>(pixel / vec2<f32>(frame.size.xy) * vec2<f32>(frame.grid.xy)), frame.grid.xy - 1u);
    let cell = tile.x + tile.y * frame.grid.x + slice * frame.grid.x * frame.grid.y;
    let start = cell * (frame.grid.w + 1u);
    let overflow = clusters[start] == 0xffffffffu;
    let count = select(clusters[start], frame.counts.x, overflow);
    for (var i = 0u; i < count; i++) {
        var index = i;
        if (!overflow) {
            index = clusters[start + 1u + i];
        }
        let p = scene_data[index * 4u];
        let d = scene_data[index * 4u + 1u];
        let radiance = scene_data[index * 4u + 2u];
        let spot = scene_data[index * 4u + 3u];
        let delta = p.xyz - position;
        let distance = length(delta);
        if (distance >= p.w) {
            continue;
        }
        let direction = delta / max(distance, 1e-4);
        if (dot(normal, direction) <= 0.0) { continue; }
        var attenuation = rendering_range_attenuation(distance, p.w);
        if (d.w > 0.5) {
            attenuation *= rendering_spot_attenuation(dot(-direction, d.xyz), spot.x, spot.y);
        }
        if (attenuation <= 0.0) {
            continue;
        }
        if (spot.z >= 0.0) {
            attenuation *= shadow_visibility(u32(spot.z), position);
        }
        color += attenuation * direct_lighting(normal, view, direction, radiance.rgb, base, metal, rough);
    }
    return color + get_indirect_lighting(position, normal, view, base, metal, rough, lightmap, lightmap_dx, lightmap_dy) * ao + emission;
}

fn shade(surface: Surface, pixel: vec2<f32>) -> vec3<f32> {
    return scene_lighting(surface.position, surface.normal, surface.base, surface.metal, surface.rough, surface.emission, surface.ao, pixel, surface.lightmap, surface.lightmap_dx, surface.lightmap_dy);
}
