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
#if DYNAMIC_TRACE
@group(0) @binding(4) var<storage, read> dynamic_tracing: array<vec4<f32>>;
#endif
#if REFERENCE_LIGHTING
struct ProbeLighting {
    sky: Sky,
    light_direction: vec4<f32>,
    light_radiance: vec4<f32>,
}
@group(0) @binding(5) var<uniform> reference: ProbeLighting;
@group(0) @binding(6) var difference_texture: texture_storage_3d<rgba16float, write>;
#endif

fn trace_record(index: u32, dynamic: bool) -> vec4<f32> {
#if DYNAMIC_TRACE
    if (dynamic) { return dynamic_tracing[index]; }
#endif
    return tracing[index];
}

#import rendering/Lighting/scene_trace

var<workgroup> probe_contributions: array<vec4<f32>,576>;

struct ProbeRay {
    radiance: vec3<f32>,
    back: f32,
}

fn probe_ray(position: vec3<f32>, direction: vec3<f32>, sky: Sky,
    light_direction: vec4<f32>, light_radiance: vec3<f32>, actors: bool) -> ProbeRay {
    let hit = trace_scene(position, direction, light_direction.w, actors, 0u);
    var radiance = sky_color(direction, sky);
    var back = 0.0;
    if (hit.triangle != 0xffffffffu) {
        let indices = vec4<u32>(trace_record(u32(trace_record(0u,hit.dynamic).y)+hit.triangle,hit.dynamic));
        let vertex_start = u32(trace_record(1u,hit.dynamic).x);
        let a = trace_record(vertex_start+indices.x,hit.dynamic).xyz;
        let b = trace_record(vertex_start+indices.y,hit.dynamic).xyz;
        let c = trace_record(vertex_start+indices.z,hit.dynamic).xyz;
        let material = u32(trace_record(0u,hit.dynamic).w)+indices.w*2u;
        var normal = normalize(cross(b-a,c-a));
        let double_sided = trace_record(material,hit.dynamic).w>0.5;
        if (!hit.front && !double_sided) { back = 1.0; }
        if (!hit.front) { normal = -normal; }
        let point = position+direction*hit.distance+normal*0.01;
        // One-bounce Lambertian cache; direct directional light is visibility tested.
        radiance = trace_record(material+1u,hit.dynamic).xyz;
        let cosine = max(dot(normal,light_direction.xyz),0.0);
        if (cosine>0.0 && trace_scene(point,light_direction.xyz,light_direction.w,actors,0u).triangle==0xffffffffu) {
            radiance += trace_record(material,hit.dynamic).xyz*light_radiance*cosine/IBL_PI;
        }
    }
    return ProbeRay(radiance, back);
}

fn reduce_probe_contributions(lane: u32) {
    workgroupBarrier();
    for (var stride=32u; stride>0u; stride/=2u) {
        if (lane<stride) {
            for (var c=0u; c<9u; c++) {
                probe_contributions[c*64u+lane] += probe_contributions[c*64u+lane+stride];
            }
        }
        workgroupBarrier();
    }
}

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
    var backs = 0.0;
#if REFERENCE_LIGHTING
    var difference: array<vec3<f32>,9>;
    var reference_backs = 0.0;
#endif
    for (var i=lane; i<samples; i+=64u) {
        let y = 1.0-2.0*(f32(i)+0.5)/f32(samples);
        let r = sqrt(max(0.0,1.0-y*y)); let theta = 2.39996323*f32(i);
        let direction = vec3<f32>(cos(theta)*r,y,sin(theta)*r);
        let live = probe_ray(position,direction,config.sky,config.light_direction,config.light_radiance.xyz,true);
        backs += live.back;
        let basis = rendering_probe_basis(direction);
        for (var c=0u; c<9u; c++) { sum[c] += live.radiance*basis[c]; }
#if REFERENCE_LIGHTING
        let baked = probe_ray(position,direction,reference.sky,reference.light_direction,reference.light_radiance.xyz,false);
        reference_backs += baked.back;
        for (var c=0u; c<9u; c++) { difference[c] += (live.radiance-baked.radiance)*basis[c]; }
#endif
    }
    for (var c=0u; c<9u; c++) {
        probe_contributions[c*64u+lane] = vec4<f32>(sum[c],f32(backs));
    }
    reduce_probe_contributions(lane);
    let valid = select(1.0,0.0,probe_contributions[0].w*2.0>f32(samples));
    if (lane==0u) {
        var coefficients: array<vec3<f32>, 9>;
        for (var c=0u; c<9u; c++) {
            let coefficient = probe_contributions[c*64u].xyz*(4.0*IBL_PI/f32(samples));
            probes[3u+index*9u+c] = vec4<f32>(coefficient,valid);
            coefficients[c] = clamp(coefficient*valid,vec3<f32>(-65504),vec3<f32>(65504));
        }
        let packed = rendering_pack_probe(coefficients,valid);
        for (var band=0u; band<RENDERING_PROBE_TEXTURE_BANDS; band++) {
            textureStore(probe_texture,vec3<i32>(coordinate+vec3<u32>(0,0,band*dim.z)),packed[band]);
        }
    }
#if REFERENCE_LIGHTING
    // Reuse the same 9216 bytes of scratch after all lanes have read live validity.
    workgroupBarrier();
    for (var c=0u; c<9u; c++) {
        probe_contributions[c*64u+lane] = vec4<f32>(difference[c],reference_backs);
    }
    reduce_probe_contributions(lane);
    if (lane==0u) {
        let paired_valid = valid*select(1.0,0.0,probe_contributions[0].w*2.0>f32(samples));
        var coefficients: array<vec3<f32>,9>;
        for (var c=0u; c<9u; c++) {
            coefficients[c] = clamp(probe_contributions[c*64u].xyz*(4.0*IBL_PI/f32(samples))*paired_valid,
                vec3<f32>(-65504),vec3<f32>(65504));
        }
        let packed = rendering_pack_probe(coefficients,paired_valid);
        for (var band=0u; band<RENDERING_PROBE_TEXTURE_BANDS; band++) {
            textureStore(difference_texture,vec3<i32>(coordinate+vec3<u32>(0,0,band*dim.z)),packed[band]);
        }
    }
#endif
}
