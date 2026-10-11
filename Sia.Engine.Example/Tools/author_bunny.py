"""Author the Stanford original PLY as one canonical Mesh and a reference wall.

Run Blender --background --threads 1 --factory-startup --python-exit-code 1
--python author_bunny.py -- --input bun_zipper.ply --identities Bunny.identities.json
--output <new-directory>. The original scan is verified and never modified.
The native Example --cook-content command independently cooks selected products.
"""

import argparse
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import sys
import uuid

sys.dont_write_bytecode = True
import bpy
from mathutils import Vector


SOURCE_BYTES = 3033195
SOURCE_SHA256 = "b1acc63bece78444aa2e15bdcc72371a201279b98c6f5d4b74c993d02f0566fe"
PRODUCTS = frozenset(("raster", "virtual", "bvh"))


def _tool(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).parent / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def read_identities(path):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate Bunny identity key")
            result[key] = value
        return result
    with Path(path).open("rb") as stream:
        encoded = stream.read(64 * 1024 + 1)
    if len(encoded) > 64 * 1024:
        raise ValueError("Bunny identity catalog exceeds 64 KiB")
    catalog = json.loads(encoded.decode("utf-8"), object_pairs_hook=pairs)
    keys = {"scene", "mesh", "source", "raster", "virtual", "bvh", "material", "instances", "attribution"}
    if not isinstance(catalog, dict) or set(catalog) != keys:
        raise ValueError("Bunny identity catalog schema mismatch")
    if not isinstance(catalog["attribution"], str) or not catalog["attribution"].strip():
        raise ValueError("Bunny attribution is required")
    if not isinstance(catalog["instances"], list) or len(catalog["instances"]) != 135:
        raise ValueError("Bunny wall requires 135 explicit instance identities")
    ids = [catalog[key] for key in sorted(keys - {"instances", "attribution"})] + catalog["instances"]
    if any(not isinstance(value, str) for value in ids):
        raise ValueError("Bunny identities require UUID strings")
    ids = [uuid.UUID(value) for value in ids]
    if any(value.int == 0 for value in ids) or len(set(ids)) != len(ids):
        raise ValueError("Bunny identities require distinct nonempty UUIDs")
    return catalog


def import_source(path):
    with Path(path).open("rb") as stream:
        encoded = stream.read(SOURCE_BYTES + 1)
    if len(encoded) != SOURCE_BYTES or hashlib.sha256(encoded).hexdigest() != SOURCE_SHA256:
        raise ValueError("Bunny input must be the verified original Stanford bun_zipper.ply")
    lines = encoded.decode("ascii").splitlines()
    header = ["ply", "format ascii 1.0", "comment zipper output", "element vertex 35947",
              "property float x", "property float y", "property float z", "property float confidence",
              "property float intensity", "element face 69451", "property list uchar int vertex_indices", "end_header"]
    if lines[:12] != header or len(lines) != 12 + 35947 + 69451:
        raise ValueError("Unexpected original Stanford PLY schema")
    positions = [tuple(float(value) for value in line.split()) for line in lines[12:12 + 35947]]
    if any(len(value) != 5 or not all(math.isfinite(item) for item in value) for value in positions):
        raise ValueError("Invalid original Bunny vertices")
    faces = [tuple(int(value) for value in line.split()) for line in lines[12 + 35947:]]
    if any(len(value) != 4 or value[0] != 3 or any(index < 0 or index >= 35947 for index in value[1:]) for value in faces):
        raise ValueError("Invalid original Bunny triangles")
    used = sorted({index for face in faces for index in face[1:]})
    if len(used) != 34834:
        raise ValueError("Unexpected original Bunny referenced vertex count")
    minimum = [min(positions[index][axis] for index in used) for axis in range(3)]
    maximum = [max(positions[index][axis] for index in used) for axis in range(3)]
    scale = 1.6 / max(high - low for low, high in zip(minimum, maximum))
    center = [(low + high) / 2 for low, high in zip(minimum, maximum)]
    engine = [Vector(tuple((positions[index][axis] - center[axis]) * scale for axis in range(3))) for index in used]
    remap = {original: index for index, original in enumerate(used)}
    triangles = [tuple(remap[index] for index in face[1:]) for face in faces]
    normals = [Vector((0, 0, 0)) for _ in used]
    for a, b, c in triangles:
        normal = (engine[b] - engine[a]).cross(engine[c] - engine[a])
        for index in (a, b, c):
            normals[index] += normal
    if any(normal.length_squared == 0 for normal in normals):
        raise ValueError("Bunny referenced vertices require nonzero area-weighted normals")
    # The scan is Y-up. Invert the source exporter's proper Z-up -> Y-up rotation.
    def blender(value):
        return value.x, -value.z, value.y
    mesh = bpy.data.meshes.new("Bunny")
    try:
        mesh.from_pydata([blender(value) for value in engine], [], triangles)
        for polygon in mesh.polygons:
            polygon.use_smooth = True
        uv = mesh.uv_layers.new(name="TextureUV")
        uv.data.foreach_set("uv", [0.0] * (len(uv.data) * 2))
        # Authored zero UVs; no bake UV is fabricated.
        mesh.normals_split_custom_set_from_vertices([blender(normal.normalized()) for normal in normals])
    except BaseException:
        bpy.data.meshes.remove(mesh)
        raise
    return mesh, tuple((high - low) * scale * 1.2 for low, high in zip(minimum, maximum))


def author(path, catalog, output, products=PRODUCTS):
    products = frozenset(products)
    if not products or not products <= PRODUCTS:
        raise ValueError("Select a nonempty subset of Raster/Virtual/BVH")
    output = Path(output).absolute()
    if output.exists() or output.is_symlink():
        raise FileExistsError("Bunny output must not already exist")
    if not output.parent.is_dir():
        raise FileNotFoundError("Bunny output requires an existing parent directory")
    mesh, spacing = import_source(path)
    owned = {"objects": []}
    try:
        return _author_mesh(mesh, spacing, catalog, output, products, owned)
    finally:
        for obj in reversed(owned["objects"]):
            bpy.data.objects.remove(obj, do_unlink=True)
        if "scene" in owned:
            bpy.data.scenes.remove(owned["scene"])
        if "collection" in owned:
            bpy.data.collections.remove(owned["collection"])
        bpy.data.meshes.remove(mesh)
        if "material" in owned:
            bpy.data.materials.remove(owned["material"])


def _author_mesh(mesh, spacing, catalog, output, products, owned):
    scene = bpy.data.scenes.new("Bunny")
    owned["scene"] = scene
    collection = bpy.data.collections.new("BunnyWall")
    owned["collection"] = collection
    scene.collection.children.link(collection)
    mesh["sia.asset_id"] = catalog["mesh"]
    material = bpy.data.materials.new("Bunny")
    owned["material"] = material
    material.use_nodes = True
    material["sia.asset_id"] = catalog["material"]
    shader = next(node for node in material.node_tree.nodes if node.type == "BSDF_PRINCIPLED")
    shader.inputs["Base Color"].default_value = (.8, .75, .65, 1)
    shader.inputs["Roughness"].default_value = .8
    mesh.materials.append(material)
    for index, (row, column) in enumerate((row, column) for row in range(-4, 5) for column in range(-7, 8)):
        obj = bpy.data.objects.new("Bunny" + str(index), mesh)
        owned["objects"].append(obj)
        obj.location = (column * spacing[0], 0, row * spacing[1])
        obj["sia.instance_id"] = catalog["instances"][index]
        collection.objects.link(obj)
    scene["sia.asset_id"], scene["sia.attribution"] = catalog["scene"], catalog["attribution"]
    output.mkdir()
    created = []
    def write(name, operation):
        target = output / name
        operation(target)
        created.append(target)
    def filename(key, suffix=".siaasset"):
        return uuid.UUID(catalog[key]).hex + suffix
    def json_file(target, value):
        # Each small recipe is completely encoded before exclusive creation.
        encoded = json.dumps(value, indent=2, sort_keys=True, allow_nan=False).encode("utf-8")
        with target.open("xb") as stream:
            created.append(target)
            stream.write(encoded)
    try:
        write(filename("source"), lambda target: _tool("export_mesh_source").export_source(obj, target))
        write(filename("material"), lambda target: _tool("export_material").export_material(material, target))
        write(filename("scene", ".siascene"), lambda target: _tool("export_scene_references").export_scene(scene, collection, target))
        recipe = {key: catalog[key] for key in ("mesh", "source")}
        recipe["input"] = filename("source")
        for kind in products:
            recipe[kind] = catalog[kind] if kind == "raster" else {"id": catalog[kind]}
        json_file(output / filename("mesh", ".mesh.json"), recipe)
        json_file(output / "content.json", {"meshes": [filename("mesh", ".mesh.json")], "scene": {
            "scene": catalog["scene"], "input": filename("scene", ".siascene"),
            **{kind: kind in products for kind in PRODUCTS},
            "materials": [{"id": catalog["material"], "input": filename("material")}], "textures": []}})
    except BaseException:
        for target in reversed(created):
            target.unlink()
        output.rmdir()
        raise


def main():
    _tool("export_mesh_source").require_deterministic_threads()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--identities", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--products", nargs="+", choices=sorted(PRODUCTS), default=sorted(PRODUCTS))
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    author(args.input, read_identities(args.identities), args.output, args.products)


if __name__ == "__main__":
    main()
