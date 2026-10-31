#define_import_path pbr/Common/surface

#import pbr/bindings

struct Surface {
    position: vec3<f32>,
    geometry_normal: vec3<f32>,
    normal: vec3<f32>,
    base: vec3<f32>,
    uv: vec2<f32>,
    bake_uv: vec2<f32>,
    bake_uv_dx: vec2<f32>,
    bake_uv_dy: vec2<f32>,
    bake_present: bool,
    source_instance: u32,
    metal: f32,
    rough: f32,
    ao: f32,
    emission: vec3<f32>,
    opacity: f32,
    transport: vec2<f32>,
    valid: bool,
}

fn sample_material(
    m: Material,
    position: vec3<f32>,
    normal_in: vec3<f32>,
    tangent: vec4<f32>,
    uv: vec2<f32>,
    uv_dx: vec2<f32>,
    uv_dy: vec2<f32>,
    front: bool,
) -> Surface {
    return sample_material_with_base(m, position, normal_in, tangent, uv, uv_dx, uv_dy, front, sample_base(m, uv, uv_dx, uv_dy));
}

// Four bits per map clamp sampling to completely published layer mips. Shared arrays
// keep batching while unrelated layers remain at their independently resident detail.
fn resident_level(uv_dx: vec2<f32>, uv_dy: vec2<f32>, dimensions: vec2<f32>, state: u32, map: u32) -> f32 {
    let minimum = (state >> (map * 4u)) & 15u;
    if ((state & (1u << (20u + map))) != 0u) {
        return f32(minimum);
    }
    if (minimum != 0u) {
        let dx = uv_dx * dimensions;
        let dy = uv_dy * dimensions;
        if (max(dot(dx, dx), dot(dy, dy)) < exp2(2.0 * f32(minimum))) {
            return f32(minimum);
        }
    }
    return -1.0;
}

fn sample_base(m: Material, uv: vec2<f32>, uv_dx: vec2<f32>, uv_dy: vec2<f32>) -> vec4<f32> {
    if ((m.indices.z & 1u) != 0u) {
        if (m.indices.w != 0u) {
            let level = resident_level(uv_dx, uv_dy, vec2<f32>(textureDimensions(albedo)), m.indices.w, 0u);
            if (level >= 0.0) { return textureSampleLevel(albedo, albedo_sampler, uv, i32(m.layers.x), level); }
        }
        return textureSampleGrad(albedo, albedo_sampler, uv, i32(m.layers.x), uv_dx, uv_dy);
    }
    return vec4<f32>(1.0);
}

fn sample_material_with_base(
    m: Material,
    position: vec3<f32>,
    normal_in: vec3<f32>,
    tangent: vec4<f32>,
    uv: vec2<f32>,
    uv_dx: vec2<f32>,
    uv_dy: vec2<f32>,
    front: bool,
    base: vec4<f32>,
) -> Surface {
    var surface: Surface;
    surface.valid = true;
    var normal = unit(normal_in);
    if (m.factors.z != 0.0) {
        let transformed = tangent.xyz;
        var t = transformed - normal * dot(normal, transformed);
        if (dot(t, t) < 1e-12) {
            t = cross(select(vec3<f32>(0, 1, 0), vec3<f32>(1, 0, 0), abs(normal.x) < .9), normal);
        }
        t = unit(t);
        var normal_sample: vec4<f32>;
        let level = resident_level(uv_dx, uv_dy, vec2<f32>(textureDimensions(normal_map)), m.indices.w, 1u);
        if (level >= 0.0) { normal_sample = textureSampleLevel(normal_map, normal_sampler, uv, i32(m.layers.y), level); }
        else { normal_sample = textureSampleGrad(normal_map, normal_sampler, uv, i32(m.layers.y), uv_dx, uv_dy); }
        let sampled = normal_sample.xyz * 2.0 - 1.0;
        normal = unit(t * (sampled.x * m.factors.x) + cross(normal, t) * select(1.0, -1.0, tangent.w < 0.0) * (sampled.y * m.factors.x) + normal * sampled.z);
    }
    if (m.factors.w != 0.0 && !front) {
        normal = -normal;
    }
    var mr = vec4<f32>(1.0);
    if ((m.indices.z & 2u) != 0u) {
        let level = resident_level(uv_dx, uv_dy, vec2<f32>(textureDimensions(mr_map)), m.indices.w, 2u);
        if (level >= 0.0) { mr = textureSampleLevel(mr_map, mr_sampler, uv, i32(m.layers.z), level); }
        else { mr = textureSampleGrad(mr_map, mr_sampler, uv, i32(m.layers.z), uv_dx, uv_dy); }
    }
    surface.position = position;
    surface.normal = normal;
    surface.base = base.rgb * m.color.rgb;
    surface.uv = uv;
    surface.metal = clamp(m.color.w * mr.b, 0.0, 1.0);
    surface.rough = clamp(m.emission.w * mr.g, .045, 1.0);
    surface.ao = 1.0;
    if ((m.indices.z & 4u) != 0u) {
        var ao_sample: vec4<f32>;
        let level = resident_level(uv_dx, uv_dy, vec2<f32>(textureDimensions(ao_map)), m.indices.w, 3u);
        if (level >= 0.0) { ao_sample = textureSampleLevel(ao_map, ao_sampler, uv, i32(m.layers.w), level); }
        else { ao_sample = textureSampleGrad(ao_map, ao_sampler, uv, i32(m.layers.w), uv_dx, uv_dy); }
        surface.ao = mix(1.0, ao_sample.r, m.factors.y);
    }
    surface.emission = m.emission.rgb;
    if ((m.indices.z & 8u) != 0u) {
        var emission_sample: vec4<f32>;
        let level = resident_level(uv_dx, uv_dy, vec2<f32>(textureDimensions(emission_map)), m.indices.w, 4u);
        if (level >= 0.0) { emission_sample = textureSampleLevel(emission_map, emission_sampler, uv, i32(m.indices.x), level); }
        else { emission_sample = textureSampleGrad(emission_map, emission_sampler, uv, i32(m.indices.x), uv_dx, uv_dy); }
        surface.emission *= emission_sample.rgb;
    }
    surface.opacity = base.a * m.transport.x;
    surface.transport = m.transport.yz;
    return surface;
}
