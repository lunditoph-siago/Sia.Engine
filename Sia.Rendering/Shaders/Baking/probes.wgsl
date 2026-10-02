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
    var backs = 0u;
    for (var i=lane; i<samples; i+=64u) {
        let y = 1.0-2.0*(f32(i)+0.5)/f32(samples);
        let r = sqrt(max(0.0,1.0-y*y)); let theta = 2.39996323*f32(i);
        let direction = vec3<f32>(cos(theta)*r,y,sin(theta)*r);
        let hit = trace_scene(position,direction,config.light_direction.w);
        var radiance = sky_color(direction,config.sky);
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
        let basis = rendering_probe_basis(direction);
        for (var c=0u; c<9u; c++) { sum[c] += radiance*basis[c]; }
    }
    for (var c=0u; c<9u; c++) {
        probe_contributions[c*64u+lane] = vec4<f32>(sum[c],f32(backs));
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
    let valid = select(1.0,0.0,probe_contributions[0].w*2.0>f32(samples));
    for (var c=0u; c<9u; c++) {
        let integrated = probe_contributions[c*64u];
        let coefficient = integrated.xyz*(4.0*IBL_PI/f32(samples));
        probes[3u+index*9u+c] = vec4<f32>(coefficient,valid);
        coefficients[c] = clamp(coefficient*valid,vec3<f32>(-65504),vec3<f32>(65504));
    }
    let packed = rendering_pack_probe(coefficients,valid);
    for (var band=0u; band<RENDERING_PROBE_TEXTURE_BANDS; band++) {
        textureStore(probe_texture,vec3<i32>(coordinate+vec3<u32>(0,0,band*dim.z)),packed[band]);
    }
}
