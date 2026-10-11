"""Export one Blender collection as SIASCNR1 references and static instances.

Authored sia.asset_id UUIDs identify Scene, Mesh manifests and Material assets;
sia.instance_id identifies each object. This tool never invents IDs, saves a
blend, exports payloads or selects/cooks/acquires derived representations.
"""

import argparse
import hashlib
import math
from pathlib import Path
import struct
import sys
import uuid

import bpy
from mathutils import Matrix


MAXIMUM_BYTES = 128 * 1024 * 1024
MAXIMUM_BINDINGS = 4096
MAXIMUM_INSTANCES = 1_000_000
MAXIMUM_MATERIAL_REFERENCES = 65536
BASIS = Matrix(((1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))


def _id(owner, key="sia.asset_id"):
    value = owner.get(key) if owner is not None else None
    if not isinstance(value, str):
        raise ValueError(f"Authored {key} UUID is required")
    result = uuid.UUID(value)
    if result.int == 0:
        raise ValueError(f"Authored {key} UUID must be nonempty")
    return result


def _transform(obj):
    transform = BASIS @ obj.matrix_world @ BASIS.transposed()
    values = tuple(transform[row][column] for column in range(4) for row in range(4))
    if not all(math.isfinite(value) for value in values):
        raise ValueError("Instance transform must be finite")
    encoded = struct.pack("<16f", *values)
    # Validate the actual float32 values written, including determinant overflow.
    values = struct.unpack("<16f", encoded)
    matrix = Matrix(tuple(tuple(values[column * 4 + row] for column in range(4)) for row in range(4)))
    determinant = matrix.determinant()
    if (not math.isfinite(determinant) or determinant <= 1e-12
            or values[3] != 0 or values[7] != 0 or values[11] != 0 or values[15] != 1):
        raise ValueError("Instance transform must be nonsingular positive affine")
    return encoded


def _metadata(scene, collection, maximum_bytes):
    if scene is None or collection is None:
        raise ValueError("Select a Scene and collection")
    scene_id = _id(scene)
    attribution = scene.get("sia.attribution", "")
    if not isinstance(attribution, str):
        raise ValueError("Scene attribution must be text")
    encoded_attribution = attribution.encode("utf-8", errors="strict")
    if len(attribution.encode("utf-16-le", errors="strict")) > 32768 or len(encoded_attribution) > 65536:
        raise ValueError("Scene attribution exceeds its domain limits")
    if not isinstance(maximum_bytes, int) or not 0 <= maximum_bytes <= MAXIMUM_BYTES:
        raise ValueError("Encoded byte limit must be between zero and 128 MiB")
    kinds, mesh_owners, material_owners = {scene_id: "scene"}, {}, {}
    bindings, instances, instance_ids = set(), [], set()
    slot_count = 0

    def claim(asset_id, kind):
        previous = kinds.setdefault(asset_id, kind)
        if previous != kind:
            raise ValueError("One logical asset ID cannot identify different asset kinds")

    for obj in collection.all_objects:
        if obj.instance_type != "NONE":
            raise ValueError("Author instancing as explicit Mesh objects before export")
        if obj.type != "MESH":
            continue
        if len(instances) == MAXIMUM_INSTANCES:
            raise ValueError("Scene instance count limit exceeded")
        if obj.modifiers or obj.constraints or obj.animation_data or obj.data.shape_keys:
            raise ValueError("Bake deformation, constraints and animation before static scene export")
        instance_id, mesh_id = _id(obj, "sia.instance_id"), _id(obj.data)
        if instance_id in instance_ids:
            raise ValueError("Instance IDs must be unique")
        instance_ids.add(instance_id)
        claim(mesh_id, "mesh")
        if mesh_owners.setdefault(mesh_id, obj.data.as_pointer()) != obj.data.as_pointer():
            raise ValueError("Distinct Mesh datablocks cannot claim one Mesh asset ID")
        slots = sorted({polygon.material_index for polygon in obj.data.polygons})
        if not 1 <= len(slots) <= 4096:
            raise ValueError("Mesh requires 1..4096 used material slots")
        materials = []
        for slot in slots:
            if slot < 0 or slot >= len(obj.material_slots):
                raise ValueError("Used material slot has no authored material")
            material = obj.material_slots[slot].material
            material_id = _id(material)
            claim(material_id, "material")
            if material_owners.setdefault(material_id, material.as_pointer()) != material.as_pointer():
                raise ValueError("Distinct Material datablocks cannot claim one material asset ID")
            materials.append((slot, material_id))
        binding = (mesh_id, tuple(materials))
        if binding not in bindings:
            if len(bindings) == MAXIMUM_BINDINGS:
                raise ValueError("Scene binding count limit exceeded")
            slot_count += len(materials)
            if slot_count > MAXIMUM_MATERIAL_REFERENCES:
                raise ValueError("Scene material reference limit exceeded")
            bindings.add(binding)
        instances.append((instance_id, binding, _transform(obj)))
        encoded_size = 64 + 20 * len(bindings) + 20 * slot_count + 84 * len(instances) + len(encoded_attribution)
        if encoded_size > maximum_bytes:
            raise ValueError("Scene exceeds its encoded byte limit")

    # IDs and slot tables determine ordering; names and collection insertion order do not.
    ordered_bindings = sorted(bindings)
    binding_indices = {binding: index for index, binding in enumerate(ordered_bindings)}
    body = bytearray()
    for mesh_id, materials in ordered_bindings:
        body.extend(mesh_id.bytes + struct.pack("<i", len(materials)))
        for slot, material_id in materials:
            body.extend(struct.pack("<i", slot) + material_id.bytes)
    for instance_id, binding, transform in sorted(instances, key=lambda value: value[0]):
        body.extend(instance_id.bytes + struct.pack("<i", binding_indices[binding]) + transform)
    body.extend(encoded_attribution)
    if 64 + len(body) > maximum_bytes:
        raise ValueError("Scene exceeds its encoded byte limit")
    header = b"SIASCNR1" + struct.pack("<4iQ", len(bindings), len(instances), slot_count,
                                       len(encoded_attribution), len(body))
    digest = hashlib.sha256(header)
    digest.update(body)
    return scene_id, (header, body, digest.digest())


def export_scene(scene, collection, output, maximum_bytes=MAXIMUM_BYTES):
    """Write only metadata to a new correctly named .siascene; return its authored ID.

    Byte admission covers encoded metadata, not total Python/Blender working
    memory. The caller separately authors source/products/shading and publishes
    the referenced assets. References do not load, build, pin or own payloads.
    """
    scene_id, blocks = _metadata(scene, collection, maximum_bytes)
    output = Path(output).absolute()
    if output.name != scene_id.hex + ".siascene":
        raise ValueError("Scene filename must be its authored UUID in N format plus .siascene")
    if output.exists() or output.is_symlink():
        raise FileExistsError("Scene output must not already exist")
    if not output.parent.is_dir():
        raise FileNotFoundError("Scene output requires an existing parent directory")
    created = False
    try:
        with output.open("xb") as stream:
            created = True
            for block in blocks:
                stream.write(block)
    except BaseException:
        if created:
            output.unlink()
        raise
    return scene_id


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scene", help="Authored Scene name; defaults to the active Scene")
    parser.add_argument("--collection", required=True, help="Explicit collection, including child collections")
    parser.add_argument("--output", required=True, type=Path, help="New <scene-UUID-N>.siascene")
    parser.add_argument("--maximum-bytes", type=int, default=MAXIMUM_BYTES)
    tail = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = parser.parse_args(tail)
    scene = bpy.data.scenes.get(args.scene) if args.scene is not None else bpy.context.scene
    scene_id = export_scene(scene, bpy.data.collections.get(args.collection), args.output, args.maximum_bytes)
    print(f"Reference scene {scene_id}: {args.output}")


if __name__ == "__main__":
    main()
