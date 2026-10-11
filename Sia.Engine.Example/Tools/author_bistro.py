"""Author canonical Bistro sources with an explicit persistent identity recipe.

Run in factory-startup background Blender. --capture-identities creates a new
identity file once; --identities reads an existing file and --output authors a
new directory. Names/relative paths are input locators, never asset identities.
Use Blender --threads 1 for reproducible source tangents. FBX import selects its
static initial pose; animation is not imported.
The original FBX/DDS files are never saved or changed. Cooking/publication uses
the emitted recipes separately through the native Example's --cook-content CLI.
"""

import argparse
import importlib.util
import json
from pathlib import Path
import sys
import uuid

sys.dont_write_bytecode = True
import bpy


MAXIMUM_IDENTITY_BYTES = 8 * 1024 * 1024
MAXIMUM_RECIPE_BYTES = 1024 * 1024
MAXIMUM_ASSETS = 4096
PRODUCTS = frozenset(("raster", "virtual", "bvh"))


def _tool(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).parent / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _inventory(collection, directory):
    directory = Path(directory).resolve()
    meshes, materials, images, instances = {}, {}, {}, {}
    policy = _tool("prepare_bistro_material")
    def image(node, role):
        if node is None:
            return
        owner = node.image
        path = Path(bpy.path.abspath(owner.filepath)).resolve()
        try:
            key = path.relative_to(directory).as_posix()
        except ValueError as error:
            raise ValueError("Bistro images must be inside the source directory") from error
        if not path.is_file():
            raise FileNotFoundError("Bistro image source is missing: " + key)
        previous = images.setdefault(key, (owner, role))
        if previous[1] != role:
            raise ValueError("One source image cannot require incompatible roles")
    for obj in collection.all_objects:
        if obj.type != "MESH":
            raise ValueError("Select a Mesh-only source collection")
        if obj.name in instances:
            raise ValueError("Source object locators must be unique")
        instances[obj.name] = obj
        mesh = obj.data
        previous = meshes.setdefault(mesh.name, obj)
        if previous.data != mesh:
            raise ValueError("Source Mesh locators must be unique")
        if mesh.uv_layers.active is None:
            raise ValueError("Source Mesh requires an authored primary UV channel")
        uv = mesh.uv_layers.active.name
        for index in {polygon.material_index for polygon in mesh.polygons}:
            if index >= len(obj.material_slots) or obj.material_slots[index].material is None:
                raise ValueError("Used source slots require materials")
            material = obj.material_slots[index].material
            previous = materials.setdefault(material.name, (material, set()))
            if previous[0] != material:
                raise ValueError("Source Material locators must be unique")
            previous[1].add(uv)
    if not instances or len(meshes) > MAXIMUM_ASSETS or len(materials) > MAXIMUM_ASSETS:
        raise ValueError("Source requires instances and at most 4096 Mesh/material entries")
    for material, uvs in materials.values():
        tree = material.node_tree
        if tree is None:
            raise ValueError("Source material requires a node graph")
        outputs = [node for node in tree.nodes if node.type == "OUTPUT_MATERIAL" and node.is_active_output]
        if len(outputs) != 1 or not outputs[0].inputs["Surface"].is_linked:
            raise ValueError("Source material requires one connected surface")
        shader = outputs[0].inputs["Surface"].links[0].from_node
        if shader.type != "BSDF_PRINCIPLED":
            raise ValueError("Source material requires a Principled shader")
        # Validate every used source UV against explicit nodes; implicit UV can
        # legitimately have different names on different Mesh datablocks.
        for uv in uvs:
            image(policy._image(shader.inputs["Base Color"], "_BaseColor", uv), "color")
            image(policy._image(shader.inputs["Emission Color"], "_Emissive", uv), "color")
            image(policy._image(shader.inputs["Specular IOR Level"], "_Specular", uv), "data")
            socket = shader.inputs["Normal"]
            if socket.is_linked:
                node = socket.links[0].from_node
                if node.type != "NORMAL_MAP" or node.space != "TANGENT" or node.uv_map not in ("", uv):
                    raise ValueError("Source normal map requires the selected tangent-space UV")
                image(policy._image(node.inputs["Color"], "_Normal", uv), "normal")
    if len(images) > MAXIMUM_ASSETS:
        raise ValueError("Source texture count limit exceeded")
    return meshes, materials, images, instances


def capture_identities(collection, directory, attribution):
    """Describe current source locators and author fresh IDs; performs no writes."""
    meshes, materials, images, instances = _inventory(collection, directory)
    new_id = lambda: str(uuid.uuid4())
    return {
        "scene": new_id(), "attribution": attribution,
        "meshes": {name: {"mesh": new_id(), "source": new_id(), "raster": new_id(),
                            "virtual": new_id(), "bvh": new_id(),
                            "textureUv": obj.data.uv_layers.active.name,
                            "bakeUv": "LightMapUV" if obj.data.uv_layers.get("LightMapUV") else None}
                   for name, obj in sorted(meshes.items())},
        "materials": {name: new_id() for name in sorted(materials)},
        "images": {key: {"id": new_id(), "role": value[1]} for key, value in sorted(images.items())},
        "instances": {name: new_id() for name in sorted(instances)},
    }


def _uuid(value):
    if not isinstance(value, str):
        raise ValueError("Identity requires an explicitly authored UUID string")
    result = uuid.UUID(value)
    if result.int == 0:
        raise ValueError("Identity UUID must be nonempty")
    return result


def _keys(value, expected):
    if not isinstance(value, dict) or set(value) != set(expected):
        raise ValueError("Identity recipe keys do not match its source/schema")


def validate_identities(catalog, inventory):
    meshes, materials, images, instances = inventory
    _keys(catalog, ("scene", "attribution", "meshes", "materials", "images", "instances"))
    if not isinstance(catalog["attribution"], str):
        raise ValueError("Attribution must be text")
    for key, expected in (("meshes", meshes), ("materials", materials), ("images", images), ("instances", instances)):
        _keys(catalog[key], expected)
    claimed = set()
    def claim(value):
        value = _uuid(value)
        if value in claimed:
            raise ValueError("Distinct source assets cannot claim the same identity")
        claimed.add(value)
    claim(catalog["scene"])
    for name, entry in catalog["meshes"].items():
        _keys(entry, ("mesh", "source", "raster", "virtual", "bvh", "textureUv", "bakeUv"))
        for kind in ("mesh", "source", "raster", "virtual", "bvh"):
            claim(entry[kind])
        mesh = meshes[name].data
        if not isinstance(entry["textureUv"], str) or entry["textureUv"] != mesh.uv_layers.active.name:
            raise ValueError("Selected source UV must match the material's primary UV")
        if entry["bakeUv"] is not None and (not isinstance(entry["bakeUv"], str)
                                           or mesh.uv_layers.get(entry["bakeUv"]) is None):
            raise ValueError("Selected source bake UV channel is missing")
    for value in catalog["materials"].values():
        claim(value)
    for key, entry in catalog["images"].items():
        _keys(entry, ("id", "role"))
        claim(entry["id"])
        if entry["role"] != images[key][1]:
            raise ValueError("Authored image role does not match its source slot")
    instance_ids = [_uuid(value) for value in catalog["instances"].values()]
    if len(instance_ids) != len(set(instance_ids)):
        raise ValueError("Scene instance identities must be unique")


def _write_json(path, value, maximum_bytes):
    encoded = json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True,
                         allow_nan=False).encode("utf-8")
    if len(encoded) > maximum_bytes:
        raise ValueError("Authoring recipe exceeds its byte admission")
    path = Path(path)
    created = False
    try:
        with path.open("xb") as stream:
            created = True
            stream.write(encoded)
    except BaseException:
        if created:
            path.unlink()
        raise


def read_identities(path):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate identity recipe key")
            result[key] = value
        return result
    with Path(path).open("rb") as stream:
        encoded = stream.read(MAXIMUM_IDENTITY_BYTES + 1)
    if len(encoded) > MAXIMUM_IDENTITY_BYTES:
        raise ValueError("Identity recipe exceeds its byte admission")
    def invalid_constant(value):
        raise ValueError("Nonfinite identity recipe constant")
    return json.loads(encoded.decode("utf-8"), object_pairs_hook=pairs, parse_constant=invalid_constant)


def author(scene, collection, directory, catalog, output, products=PRODUCTS, maximum_texture_size=256):
    """Compose canonical exporters and offline cook recipes in one new directory.

    Exclusive creation preserves existing outputs. Failures remove only created
    files and the empty owned directory. This is not power-loss-atomic publication;
    runtime publication is subsequently performed by ContentCook/ScenePack.
    """
    products = frozenset(products)
    if not products or not products <= PRODUCTS:
        raise ValueError("Choose a nonempty subset of Raster/Virtual/BVH products")
    if (isinstance(maximum_texture_size, bool) or not isinstance(maximum_texture_size, int)
            or not 1 <= maximum_texture_size <= 8192):
        raise ValueError("Texture output size must be between 1 and 8192")
    inventory = _inventory(collection, directory)
    validate_identities(catalog, inventory)
    meshes, materials, images, instances = inventory
    source_tool, material_tool, texture_tool, scene_tool = (
        _tool(name) for name in ("export_mesh_source", "export_material", "export_texture", "export_scene_references"))
    policy = _tool("prepare_bistro_material")
    scene["sia.asset_id"], scene["sia.attribution"] = catalog["scene"], catalog["attribution"]
    for name, obj in meshes.items():
        obj.data["sia.asset_id"] = catalog["meshes"][name]["mesh"]
    for name, (material, _) in materials.items():
        material["sia.asset_id"] = catalog["materials"][name]
    for name, obj in instances.items():
        obj["sia.instance_id"] = catalog["instances"][name]
    # Normalize same-file, same-role duplicate Image datablocks at this source
    # use site. Existing independent exporters keep their distinct-owner ID check.
    owners = {key: value[0] for key, value in images.items()}
    source_directory = Path(directory).resolve()
    for material, _ in materials.values():
        for node in material.node_tree.nodes:
            if node.type == "TEX_IMAGE" and node.image is not None:
                path = Path(bpy.path.abspath(node.image.filepath)).resolve()
                try:
                    key = path.relative_to(source_directory).as_posix()
                except ValueError:
                    continue
                if key in owners:
                    node.image = owners[key]
    for key, (image, role) in images.items():
        image["sia.asset_id"] = catalog["images"][key]["id"]
        image.colorspace_settings.name = "sRGB" if role == "color" else "Non-Color"
    prepared = {name: policy.prepare_material(material, sorted(uvs)[0])
                for name, (material, uvs) in materials.items()}
    # Validate all metadata/transforms before creating the output directory.
    for name, (material, uvs) in materials.items():
        material_tool._metadata(material, sorted(uvs)[0], **prepared[name])
    scene_tool._metadata(scene, collection, scene_tool.MAXIMUM_BYTES)
    output = Path(output).absolute()
    output.mkdir()
    created = []
    def target(identity, suffix=".siaasset"):
        path = output / (_uuid(identity).hex + suffix)
        return path
    def write(path, operation):
        operation()
        created.append(path)
    try:
        recipes = []
        for name, obj in sorted(meshes.items()):
            entry = catalog["meshes"][name]
            path = target(entry["source"])
            write(path, lambda: source_tool.export_source(obj, path, entry["textureUv"], entry["bakeUv"]))
            recipe = {"mesh": entry["mesh"], "source": entry["source"], "input": path.name}
            if "raster" in products:
                recipe["raster"] = entry["raster"]
            for kind in ("virtual", "bvh"):
                if kind in products:
                    recipe[kind] = {"id": entry[kind]}
            path = target(entry["mesh"], ".mesh.json")
            write(path, lambda: _write_json(path, recipe, 64 * 1024))
            recipes.append(path.name)
        material_entries, texture_entries = [], []
        for name, (material, uvs) in sorted(materials.items()):
            path = target(catalog["materials"][name])
            write(path, lambda: material_tool.export_material(material, path, sorted(uvs)[0], **prepared[name]))
            material_entries.append({"id": catalog["materials"][name], "input": path.name})
        for key, (image, role) in sorted(images.items()):
            path = target(catalog["images"][key]["id"])
            write(path, lambda: texture_tool.export_texture(image, path, role, maximum_texture_size,
                      normal_y="directx" if role == "normal" else "opengl"))
            texture_entries.append({"id": catalog["images"][key]["id"], "input": path.name})
        path = target(catalog["scene"], ".siascene")
        write(path, lambda: scene_tool.export_scene(scene, collection, path))
        recipe = {"scene": {"scene": catalog["scene"], "input": path.name,
                    **{kind: kind in products for kind in PRODUCTS},
                    "materials": material_entries, "textures": texture_entries}, "meshes": recipes}
        path = output / "content.json"
        write(path, lambda: _write_json(path, recipe, MAXIMUM_RECIPE_BYTES))
    except BaseException:
        for path in reversed(created):
            path.unlink()
        output.rmdir()
        raise
    return _uuid(catalog["scene"])


def import_source(path):
    path = Path(path).resolve()
    if path.suffix.lower() != ".fbx" or not path.is_file():
        raise ValueError("Select an existing source FBX file")
    scene = bpy.data.scenes.new("SiaBistroSource")
    bpy.context.window.scene = scene
    # This authoring path produces static Mesh records at the FBX initial pose.
    # Keep the strict independent scene exporter: do not import animation then
    # erase its ownership metadata or silently relax its static-input checks.
    if bpy.ops.import_scene.fbx(filepath=str(path), use_image_search=False, use_anim=False) != {"FINISHED"}:
        raise RuntimeError("Source FBX import did not finish")
    collection = bpy.data.collections.new("SiaBistroMeshes")
    scene.collection.children.link(collection)
    for obj in scene.objects:
        if obj.type == "MESH":
            collection.objects.link(obj)
    return scene, collection


def main():
    _tool("export_mesh_source").require_deterministic_threads()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--capture-identities", type=Path)
    mode.add_argument("--identities", type=Path)
    parser.add_argument("--attribution", help="Required when capturing identities")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--products", default="raster,virtual,bvh")
    parser.add_argument("--maximum-texture-size", type=int, default=256)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    if args.capture_identities is not None and (args.attribution is None or args.output is not None):
        raise ValueError("Identity capture requires attribution and does not author output")
    if args.identities is not None and (args.output is None or args.attribution is not None):
        raise ValueError("Existing-identity authoring requires output and retains authored attribution")
    scene, collection = import_source(args.input)
    if args.capture_identities is not None:
        catalog = capture_identities(collection, args.input.resolve().parent, args.attribution)
        validate_identities(catalog, _inventory(collection, args.input.resolve().parent))
        _write_json(args.capture_identities, catalog, MAXIMUM_IDENTITY_BYTES)
        print(f"Authored identities: {args.capture_identities}")
    else:
        scene_id = author(scene, collection, args.input.resolve().parent, read_identities(args.identities),
                          args.output, args.products.split(","), args.maximum_texture_size)
        print(f"Canonical source scene {scene_id}: {args.output}")


if __name__ == "__main__":
    main()
