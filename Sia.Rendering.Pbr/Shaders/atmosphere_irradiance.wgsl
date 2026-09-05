#import pbr::atmosphere::{sky_radiance, ATM_PI}

@group(1) @binding(0) var<storage, read_write> sh: array<vec4<f32>, 9>;

@compute @workgroup_size(9)
fn compute(@builtin(local_invocation_index) coefficient: u32) {
    var sum = vec3<f32>(0.0);
    for (var i = 0u; i < 2048u; i += 1u) {
        let y = 1.0 - 2.0 * (f32(i) + 0.5) / 2048.0;
        let phi = f32(i) * 2.39996323;
        let r = sqrt(max(1.0 - y * y, 0.0));
        let d = vec3<f32>(r * cos(phi), y, r * sin(phi));
        let basis = array<f32, 9>(0.282095, 0.488603 * d.y, 0.488603 * d.z, 0.488603 * d.x,
            1.092548 * d.x * d.y, 1.092548 * d.y * d.z, 0.315392 * (3.0 * d.z * d.z - 1.0),
            1.092548 * d.x * d.z, 0.546274 * (d.x * d.x - d.y * d.y));
        sum += sky_radiance(d) * basis[coefficient];
    }
    sh[coefficient] = vec4<f32>(sum * (4.0 * ATM_PI / 2048.0), 0.0);
}
