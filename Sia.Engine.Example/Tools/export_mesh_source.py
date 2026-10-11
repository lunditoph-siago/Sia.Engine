"""Export a static Blender Mesh directly to Sia's canonical SIAMSHS1 source.

Run in background Blender with --python-exit-code 1. The caller selects one
object and its UV channels; MeshCook recipes separately author logical IDs and
choose derived products. No scene, material payload, transform or asset owner is
created here. The input blend and its mesh data are never saved or modified.
"""

import argparse
import array
import hashlib
import math
from pathlib import Path
import struct
import sys

import bpy


MAXIMUM_BYTES = 128 * 1024 * 1024


def require_deterministic_threads():
    """Blender's parallel Mikk tangent accumulation can change float32 bytes.

    A Blender-wide thread override is a startup option, not scene render state.
    Reject the CLI invocation before emitting canonical source data.
    """
    options = sys.argv[:sys.argv.index("--")] if "--" in sys.argv else sys.argv
    script_options = ("--python", "-P", "--python-expr", "--python-console")
    first_script = next((index for index, value in enumerate(options) if value in script_options), len(options))
    options = options[:first_script]
    overrides = [options[index + 1] if index + 1 < len(options) else None
                 for index, value in enumerate(options) if value in ("--threads", "-t")]
    if not overrides or overrides[-1] != "1":
        raise ValueError("Canonical source CLI requires Blender --threads 1 before --python")


def _floats(values):
    if not all(math.isfinite(value) for value in values):
        raise ValueError("Source attributes must be finite")
    try:
        return struct.pack("<" + "f" * len(values), *values)
    except (OverflowError, struct.error) as error:
        raise ValueError("Source attributes must fit float32") from error


def _basis(vector):
    # Proper rotation from Blender Z-up to engine Y-up; winding/sign are unchanged.
    return vector[0], vector[2], -vector[1]


def _triangulated_corners(mesh, texture_uv, bake_uv):
    """Retain Blender's tessellation and corner data before tangent generation.

    calc_tangents accepts only tris/quads. Reconstruct only a temporary Mesh;
    a topology modifier/BMesh roundtrip would lose authored split normals.
    """
    result = bpy.data.meshes.new("SiaTangentTriangles")
    try:
        triangles = list(mesh.loop_triangles)
        result.from_pydata([tuple(vertex.co) for vertex in mesh.vertices], [],
                           [tuple(triangle.vertices) for triangle in triangles])
        corners = [corner for triangle in triangles for corner in triangle.loops]
        for polygon, triangle in zip(result.polygons, triangles):
            polygon.material_index = triangle.material_index
            polygon.use_smooth = True
        for name in dict.fromkeys((texture_uv, bake_uv)):
            if name is None:
                continue
            source = mesh.uv_layers[name].data
            layer = result.uv_layers.new(name=name)
            for index, corner in enumerate(corners):
                layer.data[index].uv = source[corner].uv
        normals = [tuple(mesh.corner_normals[corner].vector) for corner in corners]
        result.normals_split_custom_set(normals)
        result.calc_loop_triangles()
        return result, normals
    except BaseException:
        bpy.data.meshes.remove(result)
        raise


def export_source(obj, output, texture_uv="TextureUV", bake_uv=None,
                  maximum_bytes=MAXIMUM_BYTES):
    """Write one new local-space source file; return its semantic SHA256 digest.

    Admission bounds encoded/owned C# payload, not Blender working memory or
    Python dictionary/sort scratch. Equal corner records share a vertex; bake
    seams participate in equality. Material grouping preserves triangle order
    within each slot and retains every tessellated triangle, including degenerates.
    """
    if not isinstance(maximum_bytes, int) or not 0 <= maximum_bytes <= MAXIMUM_BYTES:
        raise ValueError("Byte limit must be between zero and 128 MiB")
    if obj is None or obj.type != "MESH":
        raise ValueError("Select a Mesh object")
    if obj.modifiers or obj.data.shape_keys:
        raise ValueError("Bake modifiers and shape keys before static source export")
    if not texture_uv or obj.data.uv_layers.get(texture_uv) is None:
        raise ValueError("The selected texture UV channel is missing")
    if bake_uv is not None and (not bake_uv or obj.data.uv_layers.get(bake_uv) is None):
        raise ValueError("The selected bake UV channel is missing")
    output = Path(output).absolute()
    if output.exists() or output.is_symlink():
        raise FileExistsError("Source output must not already exist")
    if not output.parent.is_dir():
        raise FileNotFoundError("Source output requires an existing parent directory")

    mesh = obj.data.copy()
    owned = [mesh]
    preserved_normals = None
    try:
        mesh.calc_loop_triangles()
        triangle_count = len(mesh.loop_triangles)
        if triangle_count == 0:
            raise ValueError("Source must contain triangles")
        # Reject impossible encoded/owned payloads before tangent/corner allocation.
        if 77 + 48 + triangle_count * 12 + 12 + (8 if bake_uv is not None else 0) > maximum_bytes:
            raise ValueError("Source exceeds its payload byte limit")
        if any(len(polygon.vertices) > 4 for polygon in mesh.polygons):
            mesh, preserved_normals = _triangulated_corners(mesh, texture_uv, bake_uv)
            owned.append(mesh)
        mesh.calc_tangents(uvmap=texture_uv)
        uv = mesh.uv_layers[texture_uv].data
        bake = mesh.uv_layers[bake_uv].data if bake_uv is not None else None
        vertices, bake_values = bytearray(), bytearray()
        indices = array.array("I")
        if indices.itemsize != 4:
            raise RuntimeError("Source export requires 32-bit unsigned array elements")
        sections, shared = [], {}
        minimum, maximum = [math.inf] * 3, [-math.inf] * 3
        slot, first = None, 0

        def admit(section_count):
            count = len(vertices) // 48
            payload = len(vertices) + len(indices) * 4 + section_count * 12 + len(bake_values)
            if 77 + payload > maximum_bytes or payload + count * 16 > maximum_bytes:
                raise ValueError("Source exceeds its encoded/owned payload byte limit")

        for triangle in sorted(mesh.loop_triangles, key=lambda value: value.material_index):
            if triangle.material_index != slot:
                if slot is not None:
                    sections.append((first, len(indices) - first, slot))
                slot, first = triangle.material_index, len(indices)
            for corner in triangle.loops:
                loop = mesh.loops[corner]
                normal, tangent = mesh.corner_normals[corner].vector, loop.tangent
                if preserved_normals is not None:
                    # Blender packs custom normals again in the temporary Mesh.
                    # Encode the original corner values and project its Mikk
                    # tangent onto that normal so quantization cannot accumulate.
                    normal = preserved_normals[corner]
                    norm_squared = sum(value * value for value in normal)
                    projection = sum(n * t for n, t in zip(normal, tangent)) / norm_squared if norm_squared else 0
                    tangent = tuple(t - n * projection for n, t in zip(normal, tangent))
                    length = math.sqrt(sum(value * value for value in tangent))
                    if length:
                        tangent = tuple(value / length for value in tangent)
                values = (*_basis(mesh.vertices[loop.vertex_index].co),
                          *_basis(normal), *uv[corner].uv,
                          *_basis(tangent), loop.bitangent_sign)
                record = _floats(values)
                baked = _floats(tuple(bake[corner].uv)) if bake is not None else b""
                key = record + baked
                index = shared.get(key)
                if index is None:
                    index = len(vertices) // 48
                    shared[key] = index
                    vertices.extend(record)
                    bake_values.extend(baked)
                    for axis, value in enumerate(struct.unpack("<3f", record[:12])):
                        minimum[axis] = min(minimum[axis], value)
                        maximum[axis] = max(maximum[axis], value)
                indices.append(index)
                admit(len(sections) + 1)
        sections.append((first, len(indices) - first, slot))
        admit(len(sections))
        if sys.byteorder != "little":
            indices.byteswap()
        packed_indices = indices.tobytes()
        packed_sections = b"".join(struct.pack("<3i", *section) for section in sections)
        bounds = _floats((*minimum, *maximum))
        vertex_count = len(vertices) // 48
        digest = hashlib.sha256(b"SIAMESHSOURCE1")
        digest.update(struct.pack("<4i", vertex_count, len(indices), len(sections),
                                  vertex_count if bake is not None else 0))
        for data in (bounds, vertices, packed_indices, packed_sections, bake_values):
            digest.update(data)
        header = b"SIAMSHS1" + struct.pack("<3iB", vertex_count, len(indices), len(sections),
                                           int(bake is not None)) + bounds + digest.digest()
        # Validate everything before creating output. Exclusive creation forbids overwrite.
        created = False
        try:
            with output.open("xb") as stream:
                created = True
                for data in (header, vertices, packed_indices, packed_sections, bake_values):
                    stream.write(data)
        except BaseException:
            if created:
                output.unlink()
            raise
        return digest.hexdigest().upper()
    finally:
        for temporary in reversed(owned):
            bpy.data.meshes.remove(temporary)


def main():
    require_deterministic_threads()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--object", required=True, help="Authored static Mesh object name")
    parser.add_argument("--output", required=True, type=Path, help="New canonical .siaasset file")
    parser.add_argument("--texture-uv", default="TextureUV")
    parser.add_argument("--bake-uv", help="Optional authored bake channel; omission means absent")
    parser.add_argument("--maximum-bytes", type=int, default=MAXIMUM_BYTES)
    tail = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = parser.parse_args(tail)
    digest = export_source(bpy.data.objects.get(args.object), args.output,
                           args.texture_uv, args.bake_uv, args.maximum_bytes)
    print(f"MeshSource {digest}: {args.output}")


if __name__ == "__main__":
    main()
