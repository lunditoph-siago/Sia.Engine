#define_import_path pbr/forward

#import pbr/lighting

struct ForwardVertex {
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
fn prepass_vertex(
#if RASTER_POSITION_TABLES
    @builtin(vertex_index) index: u32,
#else
    @location(0) p: vec4<f32>,
#endif
#if RESIDENT_INSTANCES
    @location(3) draw_instance: vec2<u32>,
#else
    @builtin(instance_index) instance_id: u32,
#endif
) -> @builtin(position) @invariant vec4<f32> {
#if RASTER_POSITION_TABLES
    let world = vec4<f32>(raster_position(index), 1.0);
#else
    let world = vec4<f32>(p.xyz, 1.0);
#endif
#if RESIDENT_INSTANCES
    let position = instances[draw_instance.x].transform * world;
#if RASTER_POSITION_TABLES
    return frame.vp * vec4<f32>(position.xyz, 1.0);
#else
    return frame.vp * position;
#endif
#else
    return frame.vp * world;
#endif
}

@fragment
fn prepass_fragment_depth(@builtin(position) position: vec4<f32>) -> @builtin(frag_depth) f32 {
    return position.z;
}

@vertex
fn forward_vertex(
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
) -> ForwardVertex {
#if RASTER_POSITION_TABLES
    let value = raster_vertex_value(index, draw_instance.x);
    return ForwardVertex(frame.vp * vec4<f32>(value.position, 1.0), value.position, value.normal,
        value.tangent, value.uv, instances[draw_instance.x].material.x, value.bake_uv,
        vec2<u32>(select(0u, 1u, value.bake_present), value.source_instance));
#else
    let world = vec4<f32>(p.xyz, 1.0);
#if RESIDENT_INSTANCES
    let instance = instances[draw_instance.x];
    let t = resident_tangent(words);
    let b = bitcast<vec2<f32>>(n.zw);
    let position = instance.transform * world;
    return ForwardVertex(frame.vp * position, position.xyz,
        (instance.normal_transform * vec4<f32>(resident_normal(n.y), 0.0)).xyz,
        vec4<f32>((instance.transform * vec4<f32>(t.xyz, 0.0)).xyz, t.w), vec2<f32>(p.w, bitcast<f32>(n.x)),
        instance.material.x, b, vec2<u32>(select(0u, 1u, (words.y & 2u) != 0u), instance.material.y));
#else
    return ForwardVertex(frame.vp * world, world.xyz,
        vec3<f32>(p.w, n.xy),
        t, n.zw, u32(abs(t.w)) - 1u, b.xy, vec2<u32>(u32(b.z), u32(b.w)));
#endif
#endif
}

@fragment
fn forward_fragment(
    input: ForwardVertex,
    @builtin(front_facing) front: bool,
) -> @location(0) vec4<f32> {
    var surface = sample_material(materials[input.material], input.world, input.normal,
        input.tangent, input.uv, dpdx(input.uv), dpdy(input.uv), front);
    surface.bake_uv = input.bake_uv;
    surface.bake_uv_dx = dpdx(input.bake_uv); surface.bake_uv_dy = dpdy(input.bake_uv);
    surface.bake_present = input.bake.x != 0u; surface.source_instance = input.bake.y;
    return vec4<f32>(min(shade(surface, input.position.xy) * frame.depth.z, vec3<f32>(65504)), 1.0);
}
