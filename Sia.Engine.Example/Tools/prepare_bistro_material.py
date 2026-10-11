"""Explicit ORCA Bistro source policy for an imported Blender material.

The source README specifies packed R=occlusion, G=roughness, B=metallic and
DirectX normal maps. FBX's Phong slots do not describe that convention. This
offline preparation edits only the caller's in-memory imported material. It
does not assign IDs, save files, export payloads, cook geometry or acquire assets.
Call the independent exporters afterwards; normal textures require normal_y=
'directx'. Unsupported animated/extended graphs must be authored separately.
"""

from pathlib import Path

import bpy
import numpy as np


def _image(socket, suffix, texture_uv, output="Color"):
    if not socket.is_linked:
        return None
    link = socket.links[0]
    node = link.from_node
    if node.type != "TEX_IMAGE" or node.image is None or link.from_socket.name != output:
        raise ValueError("Bistro texture slots require their direct Image Texture output")
    path = bpy.path.basename(node.image.filepath).lower()
    if not path.endswith(suffix.lower() + ".dds"):
        raise ValueError("Bistro texture filename does not match the declared source role")
    vector = node.inputs["Vector"]
    if vector.is_linked:
        source = vector.links[0]
        if (source.from_node.type != "UVMAP" or source.from_socket.name != "UV"
                or source.from_node.uv_map != texture_uv):
            raise ValueError("Bistro material requires its selected source UV channel")
    if node.projection != "FLAT" or node.extension != "REPEAT" or node.interpolation != "Linear":
        raise ValueError("Bistro source policy requires flat repeat/linear sampling")
    return node


def prepare_material(material, texture_uv, alpha_blend=None):
    """Map source Phong slots to supported PBR; return extra exporter image args.

    Dielectric IOR/specular uses the engine's 1.5/.5 approximation. Linked packed
    maps override Phong shininess/specular factors. Default alpha classification
    inspects actual source coverage; an explicit boolean overrides classification.
    Classification needs decoded source pixels, not a renderer/GPU. Extended
    wine-scene optical parameters from its .pyscene require separate authoring.
    """
    if material is None or material.node_tree is None or not texture_uv:
        raise ValueError("An imported static Bistro material and selected UV are required")
    if alpha_blend is not None and not isinstance(alpha_blend, bool):
        raise ValueError("Alpha classification override must be a boolean")
    tree = material.node_tree
    outputs = [node for node in tree.nodes if node.type == "OUTPUT_MATERIAL" and node.is_active_output]
    if len(outputs) != 1 or not outputs[0].inputs["Surface"].is_linked:
        raise ValueError("Bistro requires one directly connected Principled surface")
    shader = outputs[0].inputs["Surface"].links[0].from_node
    if shader.type != "BSDF_PRINCIPLED" or material.animation_data or tree.animation_data:
        raise ValueError("Bistro preparation requires an unanimated Principled surface")
    if outputs[0].inputs["Volume"].is_linked or outputs[0].inputs["Displacement"].is_linked:
        raise ValueError("Bistro preparation does not bake volume/displacement")
    color = _image(shader.inputs["Base Color"], "_BaseColor", texture_uv)
    emission = _image(shader.inputs["Emission Color"], "_Emissive", texture_uv)
    specular = shader.inputs["Specular IOR Level"]
    packed = _image(specular, "_Specular", texture_uv)
    if shader.inputs["Metallic"].is_linked or shader.inputs["Roughness"].is_linked:
        raise ValueError("Bistro preparation requires the original Phong import, not an already converted graph")
    normal_input = shader.inputs["Normal"]
    normal = None
    if normal_input.is_linked:
        link = normal_input.links[0]
        node = link.from_node
        if node.type != "NORMAL_MAP" or link.from_socket.name != "Normal" or node.space != "TANGENT":
            raise ValueError("Bistro requires a tangent-space Normal Map source")
        normal = _image(node.inputs["Color"], "_Normal", texture_uv)
    alpha = shader.inputs["Alpha"]
    if alpha.is_linked:
        alpha_node = _image(alpha, "_BaseColor", texture_uv, "Alpha")
        if (color is None or Path(bpy.path.abspath(alpha_node.image.filepath)).resolve()
                != Path(bpy.path.abspath(color.image.filepath)).resolve()):
            raise ValueError("Bistro alpha must reference its BaseColor image")
    if alpha_blend is None:
        alpha_blend = float(alpha.default_value) < 1 or float(shader.inputs["Transmission Weight"].default_value) > 0
        if color is not None:
            image = color.image
            width, height = image.size
            if width <= 0 or height <= 0 or len(image.pixels) != width * height * 4:
                raise ValueError("Bistro coverage classification requires complete source image pixels")
            pixels = np.empty(width * height * 4, dtype=np.float32)
            image.pixels.foreach_get(pixels)
            if not np.isfinite(pixels).all():
                raise ValueError("Bistro source pixels must be finite")
            alpha_blend |= bool(np.any(pixels[3::4] < 1))

    # Commit changes after source-slot validation and coverage classification.
    for link in list(specular.links):
        tree.links.remove(link)
    specular.default_value = .5
    shader.inputs["IOR"].default_value = 1.5
    if packed is not None:
        packed.image.colorspace_settings.name = "Non-Color"
        separate = tree.nodes.new("ShaderNodeSeparateColor")
        separate.mode = "RGB"
        tree.links.new(packed.outputs["Color"], separate.inputs["Color"])
        tree.links.new(separate.outputs["Blue"], shader.inputs["Metallic"])
        tree.links.new(separate.outputs["Green"], shader.inputs["Roughness"])
    for node in (color, emission):
        if node is not None:
            node.image.colorspace_settings.name = "sRGB"
    if normal is not None:
        normal.image.colorspace_settings.name = "Non-Color"
    elif normal_input.is_linked:
        for link in list(normal_input.links):
            tree.links.remove(link)
    if not alpha_blend:
        for link in list(alpha.links):
            tree.links.remove(link)
        alpha.default_value = 1
    elif alpha.is_linked:
        tree.links.new(color.outputs["Alpha"], alpha)
    return {"occlusion": packed.image if packed is not None else None}
