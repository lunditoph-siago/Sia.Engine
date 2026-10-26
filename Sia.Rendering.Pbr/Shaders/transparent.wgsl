#define_import_path pbr/transparent

#import pbr/lighting

struct GlassVertex {
    @builtin(position) @invariant position: vec4<f32>,
    @location(0) world: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) tangent: vec4<f32>,
    @location(3) uv: vec2<f32>,
    @location(4) @interpolate(flat, either) material: u32,
    @location(5) bake_uv: vec2<f32>,
    @location(6) @interpolate(flat, either) bake: vec2<u32>,
}

@vertex
fn transparent_vertex(
#if RASTER_POSITION_TABLES
    @builtin(vertex_index) index: u32,
    @location(3) draw_instance: vec2<u32>,
#else
    @location(0) p: vec4<f32>,
#if RESIDENT_INSTANCES
    @location(1) n: vec4<u32>,
    @location(2) words: vec2<u32>,
    @location(3) draw_instance: vec2<u32>,
#else
    @location(1) n: vec4<f32>,
    @location(2) t: vec4<f32>,
    @location(3) b: vec4<f32>,
#endif
#endif
    @builtin(instance_index) instance_id: u32,
) -> GlassVertex {
#if RASTER_POSITION_TABLES
    let value = raster_vertex_value(index, draw_instance.x);
    return GlassVertex(frame.vp * vec4<f32>(value.position, 1.0), value.position, value.normal,
        value.tangent, value.uv, instances[draw_instance.x].material.x, value.bake_uv,
        vec2<u32>(select(0u, 1u, value.bake_present), value.source_instance));
#else
    let world = vec4<f32>(p.xyz, 1.0);
#if RESIDENT_INSTANCES
    let instance = instances[draw_instance.x];
    let t = resident_tangent(words);
    let b = bitcast<vec2<f32>>(n.zw);
    let position = instance.transform * world;
    return GlassVertex(frame.vp * position, position.xyz,
        (instance.normal_transform * vec4<f32>(resident_normal(n.y), 0.0)).xyz,
        vec4<f32>((instance.transform * vec4<f32>(t.xyz, 0.0)).xyz, t.w), vec2<f32>(p.w, bitcast<f32>(n.x)),
        instance.material.x, b, vec2<u32>(select(0u, 1u, (words.y & 2u) != 0u), instance.material.y));
#elif STREAM_INSTANCES
    let instance = instances[instance_id];
    let position = instance.transform * world;
    return GlassVertex(frame.vp * position, position.xyz,
        (instance.normal_transform * vec4<f32>(p.w, n.xy, 0.0)).xyz,
        vec4<f32>((instance.transform * vec4<f32>(t.xyz, 0.0)).xyz, sign(t.w)), n.zw,
        instance.material.x, b.xy, vec2<u32>(u32(b.z), instance_id));
#else
    return GlassVertex(frame.vp * world, world.xyz, vec3<f32>(p.w, n.xy), t, n.zw, u32(abs(t.w)) - 1u,
        b.xy, vec2<u32>(u32(b.z), u32(b.w)));
#endif
#endif
}

@group(3) @binding(0) var opaque: texture_2d<f32>;
@group(3) @binding(1) var opaque_depth: texture_depth_2d;

@fragment
fn transmission_fragment(input: GlassVertex, @builtin(front_facing) front: bool) -> @location(0) vec4<f32> {
    let uv_dx = dpdx(input.uv);
    let uv_dy = dpdy(input.uv);
    let m = materials[input.material];
    if (!front && m.factors.w == 0.0) {
        discard;
    }
    let base = sample_base(m, input.uv, uv_dx, uv_dy);
    if (base.a * m.transport.x <= 0.0) { discard; }
    var s = sample_material_with_base(m, input.world, input.normal, input.tangent, input.uv, uv_dx, uv_dy, front, base);
    s.bake_uv = input.bake_uv;
    s.bake_uv_dx = dpdx(input.bake_uv); s.bake_uv_dy = dpdy(input.bake_uv);
    s.bake_present = input.bake.x != 0u; s.source_instance = input.bake.y;
    if (!s.valid || s.opacity <= 0.0) {
        discard;
    }
    var color = shade(s, input.position.xy);
    let transmission = s.transport.x * (1.0 - s.metal);
    if (transmission > 0.0) {
        let view = unit(s.position - frame.eye.xyz);
        let direction = refract(view, s.normal, 1.0 / 1.5);
        let hit = frame.vp * vec4<f32>(s.position + direction * s.transport.y, 1.0);
        let uv = hit.xy / max(hit.w, 1e-6) * vec2<f32>(.5, -.5) + .5;
        var pixel = vec2<i32>(input.position.xy);
        if (hit.w > 0.0 && all(uv >= vec2<f32>(0)) && all(uv <= vec2<f32>(1))) {
            let candidate = clamp(vec2<i32>(uv * vec2<f32>(frame.size.xy)), vec2<i32>(0), vec2<i32>(frame.size.xy) - 1);
            if (textureSampleCompareLevel(opaque_depth, depth_sampler,
                (vec2<f32>(candidate) + 0.5) / vec2<f32>(textureDimensions(opaque_depth)), input.position.z) > 0.5) {
                pixel = candidate;
            }
        }
        let background_color = textureLoad(opaque, pixel, 0).rgb / frame.depth.z;
        let f = 0.04 + 0.96 * pow(1.0 - max(dot(-view, s.normal), 0), 5.0);
        var reflected = s; reflected.base = vec3<f32>(0); reflected.metal = 0.0;
        let reflection = scene_lighting(reflected, input.position.xy);
        color = mix(color, reflection + (1.0 - f) * s.base * background_color, transmission);
    }
    return vec4<f32>(min(color * frame.depth.z, vec3<f32>(65504)), s.opacity);
}

@fragment
fn transparent_fragment(input: GlassVertex, @builtin(front_facing) front: bool) -> @location(0) vec4<f32> {
    let uv_dx = dpdx(input.uv);
    let uv_dy = dpdy(input.uv);
    let m = materials[input.material];
    let base = sample_base(m, input.uv, uv_dx, uv_dy);
    if (base.a * m.transport.x <= 0.0) { discard; }
    var s = sample_material_with_base(m, input.world, input.normal, input.tangent, input.uv, uv_dx, uv_dy, front, base);
    s.bake_uv = input.bake_uv;
    s.bake_uv_dx = dpdx(input.bake_uv); s.bake_uv_dy = dpdy(input.bake_uv);
    s.bake_present = input.bake.x != 0u; s.source_instance = input.bake.y;
    return vec4<f32>(min(shade(s, input.position.xy) * frame.depth.z, vec3<f32>(65504)), s.opacity);
}

@fragment
fn coverage_fragment(input: GlassVertex) {
    let uv_dx = dpdx(input.uv);
    let uv_dy = dpdy(input.uv);
    let m = materials[input.material];
    var opacity = m.transport.x;
    if ((m.indices.z & 1u) != 0u) {
        opacity *= textureSampleGrad(albedo, albedo_sampler, input.uv, i32(m.layers.x), uv_dx, uv_dy).a;
    }
    if (opacity < 1.0 || m.transport.y > 0.0) { discard; }
}
