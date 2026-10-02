#define_import_path pbr/transparent

#import pbr/lighting

struct GlassVertex {
    @builtin(position) @invariant position: vec4<f32>,
    @location(0) world: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) tangent: vec4<f32>,
    @location(3) uv: vec2<f32>,
    @location(4) @interpolate(flat, either) material: u32,
}

@vertex
fn transparent_vertex(
    @location(0) p: vec4<f32>,
    @location(1) n: vec4<f32>,
    @location(2) t: vec4<f32>
) -> GlassVertex {
    let world = vec4<f32>(p.xyz, 1.0);
    return GlassVertex(frame.vp * world, world.xyz, vec3<f32>(p.w, n.xy), t, n.zw, u32(abs(t.w)) - 1u);
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
    let s = sample_material_with_base(m, input.world, input.normal, input.tangent, input.uv, uv_dx, uv_dy, front, base);
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
        let background_color = textureLoad(opaque, pixel, 0).rgb;
        let f = 0.04 + 0.96 * pow(1.0 - max(dot(-view, s.normal), 0), 5.0);
        let reflection = scene_lighting(s.position, s.normal, vec3<f32>(0), 0.0, s.rough, s.emission, s.ao, input.position.xy);
        color = mix(color, reflection + (1.0 - f) * s.base * background_color, transmission);
    }
    return vec4<f32>(min(color, vec3<f32>(65504)), s.opacity);
}

@fragment
fn transparent_fragment(input: GlassVertex, @builtin(front_facing) front: bool) -> @location(0) vec4<f32> {
    let uv_dx = dpdx(input.uv);
    let uv_dy = dpdy(input.uv);
    let m = materials[input.material];
    let base = sample_base(m, input.uv, uv_dx, uv_dy);
    if (base.a * m.transport.x <= 0.0) { discard; }
    let s = sample_material_with_base(m, input.world, input.normal, input.tangent, input.uv, uv_dx, uv_dy, front, base);
    return vec4<f32>(min(shade(s, input.position.xy), vec3<f32>(65504)), s.opacity);
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
