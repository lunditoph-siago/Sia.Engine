#define_import_path rendering/Baking/probes

#import rendering/Environment/ibl
#import rendering/Lighting/probe_basis

struct ProbeConfig {
    sky: Sky,
    origin: vec4<f32>,
    step: vec4<f32>,
    shape: vec4<f32>,
    light_direction: vec4<f32>,
    light_radiance: vec4<f32>,
    update: vec4<f32>,
}

@group(0) @binding(0) var<uniform> config: ProbeConfig;
@group(0) @binding(1) var<storage, read> tracing: array<vec4<f32>>;
@group(0) @binding(2) var<storage, read_write> probes: array<vec4<f32>>;
@group(0) @binding(3) var probe_texture: texture_storage_3d<rgba16float, write>;

#ifdef PROBE_ENVIRONMENT
@group(0) @binding(9) var probe_environment: texture_cube<f32>;
@group(0) @binding(10) var probe_environment_sampler: sampler;
#endif

fn probe_sky(direction: vec3<f32>, sky: Sky, reference: bool) -> vec3<f32> {
#ifdef PROBE_ENVIRONMENT
    if (!reference) {
        return textureSampleLevel(probe_environment, probe_environment_sampler, direction, 0.0).rgb * config.shape.z;
    }
#endif
    return sky_color(direction, sky);
}

#ifdef INSTANCE_PROBE_TRACE
#import rendering/Geometry/instance_trace
@group(0) @binding(4) var<storage, read> instance_trace_geometry: array<vec4<u32>>;
@group(0) @binding(5) var<storage, read> instance_trace_instances: array<vec4<u32>>;
#ifdef PAIRED_PROBE_TRACE
@group(0) @binding(6) var<uniform> reference_config: ProbeConfig;
@group(0) @binding(7) var<storage, read> reference_surfaces: array<vec4<f32>>;
@group(0) @binding(8) var<storage, read> instance_trace_reference_instances: array<vec4<u32>>;
#endif
fn probe_surface(row: u32, reference: bool) -> vec4<f32> {
#ifdef PAIRED_PROBE_TRACE
    if (reference) { return reference_surfaces[row]; }
#endif
    return tracing[row];
}
fn instance_trace_accept_material(material: u32, reference: bool) -> bool {
#ifdef PAIRED_PROBE_TRACE
    if (reference) { return material < arrayLength(&reference_surfaces) / 2u && reference_surfaces[material * 2u + 1u].w < 0.5; }
#endif
    return material < arrayLength(&tracing) / 2u && tracing[material * 2u + 1u].w < 0.5;
}
fn trace_scene(origin: vec3<f32>, direction: vec3<f32>, maximum: f32) -> InstanceTraceHit {
    return trace_instances(origin, direction, 0.001, maximum);
}
// One frozen bake-domain bounce. Return backface validity separately from signed energy.
fn probe_incident(origin: vec3<f32>, direction: vec3<f32>, reference: bool) -> vec4<f32> {
    var lighting = config;
#ifdef PAIRED_PROBE_TRACE
    if (reference) { lighting = reference_config; }
#endif
    let hit = trace_instances_domain(origin, direction, 0.0, lighting.light_direction.w, reference);
    var radiance = probe_sky(direction, lighting.sky, reference);
    var back = 0.0;
    if (hit.instance != 0xffffffffu) {
        let material = hit.material * 2u;
        let diffuse = probe_surface(material, reference);
        let double_sided = diffuse.w > 0.5;
        radiance = vec3<f32>(0.0);
        if (!hit.front && !double_sided) { back = 1.0; }
        else {
            let normal = select(-hit.normal, hit.normal, hit.front);
            let point = origin + direction * hit.parameter + normal * lighting.light_radiance.w;
            radiance = probe_surface(material + 1u, reference).xyz;
            let cosine = max(dot(normal, lighting.light_direction.xyz), 0.0);
            if (cosine > 0.0 && trace_instances_domain(point, lighting.light_direction.xyz, 0.0,
                lighting.light_direction.w, reference).instance == 0xffffffffu) {
                radiance += diffuse.xyz * lighting.light_radiance.xyz * cosine / IBL_PI;
            }
        }
    }
    return vec4<f32>(radiance, back);
}
#else
struct TraceHit {
    distance: f32,
    triangle: u32,
    front: bool,
}

fn trace_scene(origin: vec3<f32>, direction: vec3<f32>, maximum: f32) -> TraceHit {
    var hit = TraceHit(maximum, 0xffffffffu, true);
    var node = 0u;
    let nodes = u32(tracing[0].x);
    let triangle_start = u32(tracing[0].y);
    // A tiny signed substitute keeps slab math finite for axis-parallel rays.
    let safe = select(direction, select(vec3<f32>(-1e-20), vec3<f32>(1e-20), direction >= vec3<f32>(0)), abs(direction) < vec3<f32>(1e-20));
    let inverse = 1.0 / safe;
    loop {
        if (node >= nodes) { break; }
        let lo = tracing[2u + node*3u];
        let hi = tracing[3u + node*3u];
        let a = (lo.xyz-origin)*inverse; let b = (hi.xyz-origin)*inverse;
        let near = min(a,b); let far = max(a,b);
        if (max(max(near.x,near.y),max(near.z,0.001)) > min(min(far.x,far.y),min(far.z,hit.distance))) {
            node = u32(lo.w); continue;
        }
        let count = u32(tracing[4u + node*3u].x);
        for (var i=0u; i<count; i++) {
            let triangle = u32(hi.w)+i;
            let indices = vec4<u32>(tracing[triangle_start + triangle]);
            let vertex_start = u32(tracing[1].x);
            let position = tracing[vertex_start+indices.x].xyz;
            let edge1 = tracing[vertex_start+indices.y].xyz-position;
            let edge2 = tracing[vertex_start+indices.z].xyz-position;
            let p = cross(direction,edge2); let determinant = dot(edge1,p);
            if (abs(determinant)<1e-8) { continue; }
            let t = origin-position;
            let u = dot(t,p)/determinant;
            let q = cross(t,edge1); let v = dot(direction,q)/determinant;
            let distance = dot(edge2,q)/determinant;
            // Trace both sides for conservative occlusion; backfaces mark invalid probe positions.
            if (u>=0.0 && v>=0.0 && u+v<=1.0 && distance>0.001 && distance<hit.distance) {
                hit = TraceHit(distance,triangle,determinant>0.0);
            }
        }
        node++;
    }
    return hit;
}

#endif
var<workgroup> probe_contributions: array<vec4<f32>,576>;

@compute @workgroup_size(64)
fn integrate_probes(
    @builtin(workgroup_id) invocation: vec3<u32>,
    @builtin(local_invocation_index) lane: u32
) {
    if (invocation.x >= u32(config.update.y)) { return; }
    let index = (u32(config.update.x)+invocation.x)%u32(config.shape.y);
    let dim = vec3<u32>(u32(config.origin.w),u32(config.step.w),u32(config.shape.x));
    let coordinate = vec3<u32>(index%dim.x,(index/dim.x)%dim.y,index/(dim.x*dim.y));
    let position = config.origin.xyz+vec3<f32>(coordinate)*config.step.xyz;
    let samples = u32(config.update.z);
    var sum: array<vec3<f32>,9>;
#ifdef PAIRED_PROBE_TRACE
    var live_sum: array<vec3<f32>,4>;
#endif
    var backs = 0u;
    for (var i=lane; i<samples; i+=64u) {
        let y = 1.0-2.0*(f32(i)+0.5)/f32(samples);
        let r = sqrt(max(0.0,1.0-y*y)); let theta = 2.39996323*f32(i);
        let direction = vec3<f32>(cos(theta)*r,y,sin(theta)*r);
#ifdef PAIRED_PROBE_TRACE
        let live = probe_incident(position, direction, false);
        let reference = probe_incident(position, direction, true);
        let radiance = live.xyz - reference.xyz;
        backs += u32(max(live.w, reference.w));
#else
        let hit = trace_scene(position,direction,config.light_direction.w);
        var radiance = probe_sky(direction,config.sky,false);
#ifdef INSTANCE_PROBE_TRACE
        if (hit.instance != 0xffffffffu) {
            let material = hit.material * 2u;
            var normal = hit.normal;
            let double_sided = tracing[material].w > 0.5;
            if (!hit.front && !double_sided) { backs++; }
            if (!hit.front) { normal = -normal; }
            let point = position + direction * hit.parameter + normal * 0.01;
            radiance = tracing[material + 1u].xyz;
            let cosine = max(dot(normal, config.light_direction.xyz), 0.0);
            if (cosine > 0.0 && trace_scene(point, config.light_direction.xyz, config.light_direction.w).instance == 0xffffffffu) {
                radiance += tracing[material].xyz * config.light_radiance.xyz * cosine / IBL_PI;
            }
        }
#else
        if (hit.triangle != 0xffffffffu) {
            let indices = vec4<u32>(tracing[u32(tracing[0].y)+hit.triangle]);
            let vertex_start = u32(tracing[1].x);
            let a = tracing[vertex_start+indices.x].xyz;
            let b = tracing[vertex_start+indices.y].xyz;
            let c = tracing[vertex_start+indices.z].xyz;
            let material = u32(tracing[0].w)+indices.w*2u;
            var normal = normalize(cross(b-a,c-a));
            let double_sided = tracing[material].w>0.5;
            if (!hit.front && !double_sided) { backs++; }
            if (!hit.front) { normal = -normal; }
            let point = position+direction*hit.distance+normal*0.01;
            // One-bounce Lambertian cache; direct directional light is visibility tested.
            radiance = tracing[material+1u].xyz;
            let cosine = max(dot(normal,config.light_direction.xyz),0.0);
            if (cosine>0.0 && trace_scene(point,config.light_direction.xyz,config.light_direction.w).triangle==0xffffffffu) {
                radiance += tracing[material].xyz*config.light_radiance.xyz*cosine/IBL_PI;
            }
        }
#endif
#endif
        let basis = rendering_probe_basis(direction);
#ifdef PAIRED_PROBE_TRACE
        // Surface bakes use L1; higher bands must not introduce a second estimator.
        for (var c=0u; c<4u; c++) { sum[c] += radiance*basis[c]; live_sum[c] += live.xyz*basis[c]; }
#else
        for (var c=0u; c<9u; c++) { sum[c] += radiance*basis[c]; }
#endif
    }
    for (var c=0u; c<9u; c++) {
        var contribution = sum[c];
#ifdef PAIRED_PROBE_TRACE
        if (c>=4u && c<8u) { contribution = live_sum[c-4u]; }
#endif
        probe_contributions[c*64u+lane] = vec4<f32>(contribution,f32(backs));
    }
    workgroupBarrier();
    for (var stride=32u; stride>0u; stride/=2u) {
        if (lane<stride) {
            for (var c=0u; c<9u; c++) {
                probe_contributions[c*64u+lane] += probe_contributions[c*64u+lane+stride];
            }
        }
        workgroupBarrier();
    }
    if (lane!=0u) { return; }
    var coefficients: array<vec3<f32>, 9>;
#ifdef PAIRED_PROBE_TRACE
    var live_coefficients: array<vec3<f32>, 9>;
#endif
    var valid = select(1.0,0.0,probe_contributions[0].w*2.0>f32(samples));
    let texture_scale = max(config.shape.w, 1.0);
    for (var c=0u; c<9u; c++) {
        let integrated = probe_contributions[c*64u];
        var coefficient = integrated.xyz*(4.0*IBL_PI/f32(samples));
#ifdef PAIRED_PROBE_TRACE
        if (c>=4u) { coefficient = vec3<f32>(0.0); }
        else {
            let absolute = probe_contributions[(c+4u)*64u].xyz*(4.0*IBL_PI/f32(samples));
            live_coefficients[c] = absolute;
            if (any(absolute != absolute) || any(abs(absolute / texture_scale) > vec3<f32>(65504.0))) { valid = 0.0; }
        }
#endif
        coefficients[c] = coefficient;
        if (any(coefficient != coefficient) || any(abs(coefficient / texture_scale) > vec3<f32>(65504.0))) { valid = 0.0; }
    }
    for (var c=0u; c<9u; c++) {
        probes[3u+index*9u+c] = vec4<f32>(select(vec3<f32>(0.0), coefficients[c], valid > 0.0), valid);
        coefficients[c] = select(vec3<f32>(0.0), coefficients[c] / texture_scale, valid > 0.0);
#ifdef PAIRED_PROBE_TRACE
        live_coefficients[c] = select(vec3<f32>(0.0), live_coefficients[c] / texture_scale, valid > 0.0);
#endif
    }
    let packed = rendering_pack_probe(coefficients,valid);
    for (var band=0u; band<RENDERING_PROBE_TEXTURE_BANDS; band++) {
        textureStore(probe_texture,vec3<i32>(coordinate+vec3<u32>(0,0,band*dim.z)),packed[band]);
    }
#ifdef PAIRED_PROBE_TRACE
    let absolute_packed = rendering_pack_probe(live_coefficients,valid);
    for (var band=0u; band<RENDERING_PROBE_TEXTURE_BANDS; band++) {
        textureStore(probe_texture,vec3<i32>(coordinate+vec3<u32>(0,0,(band+RENDERING_PROBE_TEXTURE_BANDS)*dim.z)),absolute_packed[band]);
    }
#endif
}
