#define_import_path pbr/exposure

struct ExposurePartial {
    log_sum: f32,
    count: u32,
}

@group(0) @binding(0) var<storage, read_write> exposure_output: vec4<f32>;
@group(0) @binding(1) var exposure_hdr: texture_2d<f32>;
@group(0) @binding(2) var<storage, read_write> exposure_partials: array<ExposurePartial, 256>;
@group(0) @binding(3) var<uniform> exposure_configuration: vec4<f32>;

var<workgroup> exposure_logs: array<f32, 64>;
var<workgroup> exposure_counts: array<u32, 64>;

fn exposure_group_count() -> vec2<u32> {
    return min((textureDimensions(exposure_hdr) + vec2<u32>(7u)) / 8u, vec2<u32>(16u));
}

fn exposure_sum_workgroup(lane: u32) {
    for (var step = 32u; step != 0u; step >>= 1u) {
        workgroupBarrier();
        if (lane < step) {
            exposure_logs[lane] += exposure_logs[lane + step];
            exposure_counts[lane] += exposure_counts[lane + step];
        }
    }
    workgroupBarrier();
}

@compute @workgroup_size(8, 8)
fn measure_exposure(@builtin(global_invocation_id) id: vec3<u32>,
    @builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let dimensions = textureDimensions(exposure_hdr);
    let groups = exposure_group_count();
    let stride = groups * 8u;
    // Legal HDR storage factors extend down to 2^-100. Keep their physical units intact.
    let inverse_scale = max(exposure_output.z, 1.17549435e-38);
    var log_sum = 0.0;
    var count = 0u;
    for (var y = id.y; y < dimensions.y; y += stride.y) {
        for (var x = id.x; x < dimensions.x; x += stride.x) {
            let physical = max(textureLoad(exposure_hdr, vec2<i32>(i32(x), i32(y)), 0).rgb, vec3<f32>(0)) / inverse_scale;
            let luminance = max(dot(physical, vec3<f32>(0.2126, 0.7152, 0.0722)), 1e-6);
            log_sum += log2(luminance);
            count++;
        }
    }
    exposure_logs[lane] = log_sum;
    exposure_counts[lane] = count;
    exposure_sum_workgroup(lane);
    if (lane == 0u) {
        exposure_partials[group.y * groups.x + group.x] = ExposurePartial(exposure_logs[0], exposure_counts[0]);
    }
}

@compute @workgroup_size(64)
fn reduce_exposure(@builtin(local_invocation_index) lane: u32) {
    let groups = exposure_group_count();
    let partial_count = groups.x * groups.y;
    var log_sum = 0.0;
    var count = 0u;
    for (var at = lane; at < partial_count; at += 64u) {
        log_sum += exposure_partials[at].log_sum;
        count += exposure_partials[at].count;
    }
    exposure_logs[lane] = log_sum;
    exposure_counts[lane] = count;
    exposure_sum_workgroup(lane);
    if (lane == 0u) {
        var stops = exposure_output.w;
        if (exposure_counts[0] != 0u) {
            let mean = exposure_logs[0] / f32(exposure_counts[0]);
            // Positive subnormal targets already saturate the allowed stop range.
            // Keep log2 defined on backends that flush their uniform inputs to zero.
            stops += log2(max(exposure_configuration.x, 1.17549435e-38)) - mean;
        }
        stops = clamp(stops, exposure_configuration.y, exposure_configuration.z);
        // z and w are the CPU-authored storage factor and compensation; x is never feedback.
        exposure_output.x = exp2(stops) / max(exposure_output.z, 1.17549435e-38);
    }
}
