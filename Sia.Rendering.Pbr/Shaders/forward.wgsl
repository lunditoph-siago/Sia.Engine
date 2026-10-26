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
    @location(1) n: vec4<u32>,
    @location(2) t: vec4<u32>,
    @location(3) b: u32,
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
#elif STREAM_INSTANCES
    let instance = instances[instance_id];
    let position = instance.transform * world;
    return ForwardVertex(frame.vp * position, position.xyz,
        (instance.normal_transform * vec4<f32>(stream_direction(n.x, n.y), 0.0)).xyz,
        vec4<f32>((instance.transform * vec4<f32>(stream_direction(t.x, t.y), 0.0)).xyz, sign(bitcast<f32>(t.z))),
        vec2<f32>(p.w, bitcast<f32>(n.z)), instance.material.x, bitcast<vec2<f32>>(vec2<u32>(n.w, t.w)),
        vec2<u32>(select(0u, 1u, (b & 0x80000000u) != 0u), instance_id));
#else
    return ForwardVertex(frame.vp * world, world.xyz,
        stream_direction(n.x, n.y),
        vec4<f32>(stream_direction(t.x, t.y), bitcast<f32>(t.z)), vec2<f32>(p.w, bitcast<f32>(n.z)),
        u32(abs(bitcast<f32>(t.z))) - 1u, bitcast<vec2<f32>>(vec2<u32>(n.w, t.w)),
        vec2<u32>(select(0u, 1u, (b & 0x80000000u) != 0u), b & 0x7fffffffu));
#endif
#endif
}

fn forward_color(
    input: ForwardVertex,
    front: bool,
) -> vec4<f32> {
    var surface = sample_material(materials[input.material], input.world, input.normal,
        input.tangent, input.uv, dpdx(input.uv), dpdy(input.uv), front);
    surface.bake_uv = input.bake_uv;
    surface.bake_uv_dx = dpdx(input.bake_uv); surface.bake_uv_dy = dpdy(input.bake_uv);
    surface.bake_present = input.bake.x != 0u; surface.source_instance = input.bake.y;
    return vec4<f32>(min(shade(surface, input.position.xy) * frame.depth.z, vec3<f32>(65504)), 1.0);
}

@fragment
fn forward_fragment(input: ForwardVertex, @builtin(front_facing) front: bool) -> @location(0) vec4<f32> {
    return forward_color(input, front);
}

struct ForwardDepthOutput {
    @location(0) color: vec4<f32>,
    @builtin(frag_depth) depth: f32,
}

@fragment
fn forward_fragment_depth(input: ForwardVertex, @builtin(front_facing) front: bool) -> ForwardDepthOutput {
    return ForwardDepthOutput(forward_color(input, front), input.position.z);
}
