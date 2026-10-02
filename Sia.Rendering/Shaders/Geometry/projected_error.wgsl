#define_import_path rendering/Geometry/projected_error

fn sphere_pixel_error(center: vec3<f32>, radius: f32, spatial: f32,
    eye_near: vec4<f32>, forward_pixels: vec4<f32>) -> f32 {
    if (spatial == 0.0) { return 0.0; }
    if (dot(forward_pixels.xyz, forward_pixels.xyz) == 0.0) { return spatial * forward_pixels.w; }
    let relative = center - eye_near.xyz;
    let distance_squared = dot(relative, relative);
    let z = dot(relative, forward_pixels.xyz);
    let radial = sqrt(max(0.0, distance_squared - z * z));
    let tangent = sqrt(max(0.0, distance_squared - radius * radius));
    var cosine = (z * tangent - radial * radius) / max(distance_squared, 1.0e-20);
    let near = eye_near.w;
    if (distance_squared <= radius * radius || cosine * tangent < near) {
        let h = near - z;
        let far_intersection = radial + sqrt(max(0.0, radius * radius - h * h));
        cosine = near / sqrt(far_intersection * far_intersection + near * near);
    }
    let edge_scale = max(z - radius, near) * max(cosine, 1.0e-6);
    return min(spatial * forward_pixels.w / edge_scale, 1.0e30);
}

fn bounds_plane_visible(center: vec3<f32>, extent: vec3<f32>, plane: vec4<f32>) -> bool {
    let distance = dot(center, plane.xyz) + plane.w + dot(extent, abs(plane.xyz));
    let tolerance = 1.0e-5 * (1.0 + dot(abs(center) + extent, abs(plane.xyz)) + abs(plane.w));
    return distance != distance || abs(distance) > 3.0e38 || distance >= -tolerance;
}

fn bounds_visible(lo: vec3<f32>, hi: vec3<f32>, vp: mat4x4<f32>) -> bool {
    let center = (lo + hi) * 0.5;
    let extent = (hi - lo) * 0.5;
    let x = vec4<f32>(vp[0].x, vp[1].x, vp[2].x, vp[3].x);
    let y = vec4<f32>(vp[0].y, vp[1].y, vp[2].y, vp[3].y);
    let z = vec4<f32>(vp[0].z, vp[1].z, vp[2].z, vp[3].z);
    let w = vec4<f32>(vp[0].w, vp[1].w, vp[2].w, vp[3].w);
    return bounds_plane_visible(center, extent, w + x) && bounds_plane_visible(center, extent, w - x)
        && bounds_plane_visible(center, extent, w + y) && bounds_plane_visible(center, extent, w - y)
        && bounds_plane_visible(center, extent, z) && bounds_plane_visible(center, extent, w - z);
}

fn bounds_pixel_error(lo: vec3<f32>, hi: vec3<f32>, spatial: f32,
    vp: mat4x4<f32>, size: vec2<f32>) -> f32 {
    if (!bounds_visible(lo, hi, vp)) { return -1.0; }
    if (spatial == 0.0) { return 0.0; }
    var min_w = 3.0e38;
    var min_z = 3.0e38;
    var max_xy = vec2<f32>(0.0);
    var crosses_eye = false;
    for (var c = 0u; c < 8u; c++) {
        let p = vec3<f32>(select(lo.x, hi.x, (c & 1u) != 0u),
            select(lo.y, hi.y, (c & 2u) != 0u), select(lo.z, hi.z, (c & 4u) != 0u));
        let v = vp * vec4<f32>(p, 1.0);
        if (any(v != v) || any(abs(v) > vec4<f32>(3.0e38))) { return 1.0e30; }
        crosses_eye = crosses_eye || v.w <= 0.0;
        min_w = min(min_w, v.w);
        min_z = min(min_z, v.z);
        max_xy = max(max_xy, abs(v.xy) / max(v.w, 1.0e-20));
    }
    let row_x = vec3<f32>(vp[0].x, vp[1].x, vp[2].x);
    let row_y = vec3<f32>(vp[0].y, vp[1].y, vp[2].y);
    let row_z = vec3<f32>(vp[0].z, vp[1].z, vp[2].z);
    let row_w = vec3<f32>(vp[0].w, vp[1].w, vp[2].w);
    let error = spatial * 1.0001;
    let w_error = error * length(row_w);
    if (crosses_eye || min_w <= w_error || min_z <= error * length(row_z)) { return 1.0e30; }
    let delta = error * (vec2<f32>(length(row_x), length(row_y)) + max_xy * length(row_w));
    let pixels = size * delta;
    return 0.5 * max(pixels.x, pixels.y) / (min_w - w_error);
}

