"""Author one SIAMATR1 material without exporting textures or scene payloads.

Run inside Blender. Authored sia.asset_id UUIDs identify the Material and Image
assets. The active surface must be a basic Principled shader; graphs requiring
baking are rejected. Image pixels, cooking, publication and asset ownership stay
in separate caller workflows. This script never saves or changes the blend.
"""

import argparse
import hashlib
import math
from pathlib import Path
import struct
import sys
import uuid

import bpy


def _id(owner):
    value = owner.get("sia.asset_id") if owner is not None else None
    if not isinstance(value, str):
        raise ValueError("An authored sia.asset_id UUID is required")
    result = uuid.UUID(value)
    if result.int == 0:
        raise ValueError("Asset UUID must be nonempty")
    return result


def _constant(shader, name):
    socket = next((item for item in shader.inputs if item.name == name), None)
    if socket is None:
        raise ValueError(f"Unsupported shader input {name}; available: {', '.join(item.name for item in shader.inputs)}")
    if socket.is_linked:
        raise ValueError(f"{name} must be a supported unlinked input; bake its graph first")
    return socket.default_value


def _image(socket, texture_uv):
    if not socket.is_linked:
        return None
    link = socket.links[0]
    node = link.from_node
    if node.type != "TEX_IMAGE" or node.image is None or link.from_socket.name != "Color":
        raise ValueError("Texture slots require a direct Image Texture Color output")
    if node.projection != "FLAT" or node.extension != "REPEAT" or node.interpolation != "Linear":
        raise ValueError("Image references require flat projection and the default repeat/linear sampler")
    vector = node.inputs["Vector"]
    if vector.is_linked:
        source = vector.links[0]
        if not (source.from_node.type == "UVMAP" and source.from_node.uv_map == texture_uv
                and source.from_socket.name == "UV"):
            raise ValueError("Texture coordinates must be the selected UV Map or the implicit primary UV")
    return node


def _metadata(material, texture_uv):
    if material is None or material.node_tree is None or not texture_uv:
        raise ValueError("Select a node-based Material and its primary UV channel")
    material_id = _id(material)
    tree = material.node_tree
    if material.animation_data or tree.animation_data:
        raise ValueError("Bake material animation before static export")
    outputs = [node for node in tree.nodes if node.type == "OUTPUT_MATERIAL" and node.is_active_output]
    if len(outputs) != 1:
        raise ValueError("One active Material Output is required")
    output = outputs[0]
    if output.inputs["Volume"].is_linked or output.inputs["Displacement"].is_linked:
        raise ValueError("Volume and displacement graphs require baking")
    surface = output.inputs["Surface"]
    if not surface.is_linked or surface.links[0].from_node.type != "BSDF_PRINCIPLED":
        raise ValueError("Active Surface must directly reference a Principled shader")
    shader = surface.links[0].from_node
    # These features have no equivalent in the current metallic/roughness material.
    for name in ("Subsurface Weight", "Anisotropic", "Coat Weight", "Sheen Weight", "Diffuse Roughness", "Thin Film Thickness"):
        if _constant(shader, name) != 0:
            raise ValueError(f"{name} requires a supported bake/authoring approximation")
    if _constant(shader, "IOR") != 1.5 or _constant(shader, "Specular IOR Level") != .5:
        raise ValueError("Current material uses fixed dielectric IOR/specular defaults")
    if tuple(_constant(shader, "Specular Tint"))[:3] != (1, 1, 1):
        raise ValueError("Specular tint requires a supported bake/authoring approximation")
    if _constant(shader, "Thin Wall"):
        raise ValueError("Thin-wall mode requires explicit supported authoring")

    color = _image(shader.inputs["Base Color"], texture_uv)
    emission = _image(shader.inputs["Emission Color"], texture_uv)
    normal, normal_scale = None, 1.0
    normal_input = shader.inputs["Normal"]
    if normal_input.is_linked:
        link = normal_input.links[0]
        node = link.from_node
        if (node.type != "NORMAL_MAP" or link.from_socket.name != "Normal" or node.space != "TANGENT"
                or node.uv_map not in ("", texture_uv)):
            raise ValueError("Normal requires a tangent-space Normal Map for the selected UV")
        normal = _image(node.inputs["Color"], texture_uv)
        if normal is None:
            raise ValueError("Normal Map requires an authored Image Texture")
        normal_scale = _constant(node, "Strength")

    alpha = shader.inputs["Alpha"]
    if alpha.is_linked:
        link = alpha.links[0]
        if color is None or link.from_node != color or link.from_socket.name != "Alpha":
            raise ValueError("Linked alpha must be the Base Color Image Texture's Alpha output")
        opacity = 1.0
    else:
        opacity = float(alpha.default_value)
    transmission = float(_constant(shader, "Transmission Weight"))
    thickness = material.get("sia.thickness", 0.0)
    if isinstance(thickness, bool) or not isinstance(thickness, (int, float)):
        raise ValueError("sia.thickness must be a numeric world-space slab thickness")
    values = (*((1, 1, 1) if color else tuple(_constant(shader, "Base Color"))[:3]),
              float(_constant(shader, "Metallic")), float(_constant(shader, "Roughness")),
              *((1, 1, 1) if emission else tuple(_constant(shader, "Emission Color"))[:3]),
              float(_constant(shader, "Emission Strength")), float(normal_scale), 1.0,
              opacity, transmission, float(thickness))
    if not all(math.isfinite(value) for value in values):
        raise ValueError("Material parameters must be finite")
    try:
        parameters = struct.pack("<14f", *values)
    except (OverflowError, struct.error) as error:
        raise ValueError("Material parameters must fit Float32") from error
    values = struct.unpack("<14f", parameters)
    if (not all(math.isfinite(value) for value in values) or values[8] < 0
            or any(not 0 <= values[index] <= 1 for index in (3, 4, 10, 11, 12)) or not 0 <= values[13] <= 10):
        raise ValueError("Material parameters exceed the existing domain bounds")

    owners, roles = {}, {}
    def reference(node, srgb):
        if node is None:
            return bytes(16)
        image = node.image
        asset_id = _id(image)
        if asset_id == material_id:
            raise ValueError("Material and texture cannot claim one asset ID")
        if owners.setdefault(asset_id, image.as_pointer()) != image.as_pointer():
            raise ValueError("Distinct Images cannot claim one texture asset ID")
        if roles.setdefault(asset_id, srgb) != srgb:
            raise ValueError("One texture ID cannot require both linear and sRGB decoding")
        expected = "sRGB" if srgb else "Non-Color"
        if image.colorspace_settings.name != expected:
            raise ValueError(f"Texture role requires the authored {expected} color space")
        return asset_id.bytes

    flags = int(not material.use_backface_culling) | (int(alpha.is_linked or opacity < 1 or transmission > 0) << 1)
    body = (b"SIAMATR1" + bytes((flags,)) + parameters + reference(color, True)
            + reference(normal, False) + bytes(32) + reference(emission, True))
    return material_id, body + hashlib.sha256(body).digest()


def export_material(material, output, texture_uv="TextureUV"):
    """Write exactly 177 metadata bytes to one new <material-UUID-N>.siaasset."""
    material_id, payload = _metadata(material, texture_uv)
    output = Path(output).absolute()
    if output.name != material_id.hex + ".siaasset":
        raise ValueError("Material filename must be its authored UUID in N format plus .siaasset")
    if output.exists() or output.is_symlink():
        raise FileExistsError("Material output must not already exist")
    if not output.parent.is_dir():
        raise FileNotFoundError("Material output requires an existing parent directory")
    created = False
    try:
        with output.open("xb") as stream:
            created = True
            stream.write(payload)
    except BaseException:
        if created:
            output.unlink()
        raise
    return material_id


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--material", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--texture-uv", default="TextureUV")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    material_id = export_material(bpy.data.materials.get(args.material), args.output, args.texture_uv)
    print(f"Material {material_id}: {args.output}")


if __name__ == "__main__":
    main()
