#define_import_path pbr/shadow_coverage

#import pbr/Common/surface

// Dynamic draw metadata: local instance, stable shadow slot, material, source
// instance seed. Fixed direct draws use firstInstance=0; nonindexed vertex
// pulling preserves every vertex encoding and the original triangle identity.
@group(3) @binding(0) var<uniform> coverage_draw: vec4<u32>;
// The exact-cut compute pass publishes this layer's rebuild bit. Nongated
// raster paths initialize it to one. Metadata and stochastic identity stay unchanged.
@group(3) @binding(1) var<uniform> coverage_state: vec4<u32>;

struct ShadowCoverageVertex {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) @interpolate(flat, either) identity: vec3<u32>,
}

@vertex fn alpha_shadow_vertex(@builtin(vertex_index) corner: u32) -> ShadowCoverageVertex {
    if (coverage_state.z == 0u) {
        return ShadowCoverageVertex(vec4<f32>(2.0, 2.0, 2.0, 1.0), vec2<f32>(0.0), vec3<u32>(0u));
    }
    let index = topology[corner];
    var position: vec3<f32>; var uv: vec2<f32>;
#if RASTER_POSITION_TABLES
    let value = raster_vertex_value(index, coverage_draw.x);
    position = value.position; uv = value.uv;
#elif RESIDENT_INSTANCES
    let p = vertex_plane(index);
    let n = resident_vec4(frame.geometry.x * 4u + index * 4u);
    position = (instances[coverage_draw.x].transform * vec4<f32>(p.xyz, 1.0)).xyz;
    uv = vec2<f32>(p.w, bitcast<f32>(n.x));
#else
    let value = stream_vertex_value(index);
#if STREAM_INSTANCES
    position = (instances[coverage_draw.x].transform * vec4<f32>(value.position, 1.0)).xyz;
#else
    position = value.position;
#endif
    uv = value.uv;
#endif
    return ShadowCoverageVertex(shadow_matrix(coverage_draw.y) * vec4<f32>(position, 1.0), uv,
        vec3<u32>(coverage_draw.z, coverage_draw.w, corner / 3u));
}

fn coverage_hash(input: u32) -> u32 {
    var value = input; value ^= value >> 16u; value *= 0x7feb352du;
    value ^= value >> 15u; value *= 0x846ca68bu; return value ^ (value >> 16u);
}

fn coverage_test(input: ShadowCoverageVertex) {
    let dx = dpdx(input.uv); let dy = dpdy(input.uv);
    let m = materials[input.identity.x];
    let base = sample_base(m, input.uv, dx, dy);
    let alpha = clamp(base.a * m.transport.x, 0.0, 1.0);
    if (!(alpha > 0.0)) { discard; }
    // This narrow coverage path preserves the current unshadowed treatment of
    // genuinely transmitting pixels. A metallic alpha material has no effective
    // transmission and retains its exact authored alpha. No glass cutoff or
    // arbitrary scalar opacity downgrade substitutes for colored transport.
    if (m.transport.y > 0.0) {
        var metallic = m.color.w;
        if ((m.indices.z & 2u) != 0u) {
            let level = resident_level(dx, dy, vec2<f32>(textureDimensions(mr_map)), m.indices.w, 2u);
            var mr: vec4<f32>;
            if (level >= 0.0) { mr = textureSampleLevel(mr_map, mr_sampler, input.uv, i32(m.layers.z), level); }
            else { mr = textureSampleGrad(mr_map, mr_sampler, input.uv, i32(m.layers.z), dx, dy); }
            metallic *= mr.b;
        }
        if (m.transport.y * (1.0 - clamp(metallic, 0.0, 1.0)) > 0.0) { discard; }
    }
    if (alpha >= 1.0) { return; }
    let pixel = vec2<u32>(input.position.xy);
    var value = coverage_hash(pixel.x ^ 0x9e3779b9u);
    value = coverage_hash(value ^ pixel.y); value = coverage_hash(value ^ coverage_draw.y);
    value = coverage_hash(value ^ input.identity.y); value = coverage_hash(value ^ input.identity.z);
    let threshold = f32(value >> 8u) * (1.0 / 16777216.0);
    if (threshold >= alpha) { discard; }
}

@fragment fn alpha_shadow_fragment(input: ShadowCoverageVertex) { coverage_test(input); }
@fragment fn alpha_shadow_fragment_depth(input: ShadowCoverageVertex) -> @builtin(frag_depth) f32 {
    coverage_test(input); return input.position.z;
}
