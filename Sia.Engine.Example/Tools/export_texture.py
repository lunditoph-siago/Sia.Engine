"""Author one SIATEXR1 RGBA8 texture with explicit color/data/normal mip policy.

Run inside Blender with its bundled NumPy. A static byte-buffer Image owns an
authored sia.asset_id UUID; this tool reads its pixels without resizing/changing
the Image, saving the blend or loading/acquiring any engine asset. Scene/material
composition and publication stay separate use-site operations.
"""

import argparse
import hashlib
from pathlib import Path
import struct
import sys
import uuid

import bpy
import numpy as np


MAXIMUM_BYTES = 128 * 1024 * 1024
MAXIMUM_DIMENSION = 8192


def _area_axis(values, size, axis):
    """Exact area average of piecewise constant source texels, including odd edges."""
    original = values.shape[axis]
    if size == original:
        return values
    if original == size * 2:
        # Common mip step: two array views avoid Float64 prefix/sampling scratch.
        even, odd = [slice(None)] * values.ndim, [slice(None)] * values.ndim
        even[axis], odd[axis] = slice(0, original, 2), slice(1, original, 2)
        return (values[tuple(even)] + values[tuple(odd)]) * .5
    edges = np.linspace(0, original, size + 1, dtype=np.float64)
    cells = np.floor(edges).astype(np.intp)
    fractions = edges - cells
    shape = [1] * values.ndim
    shape[axis] = size + 1
    zero_shape = list(values.shape)
    zero_shape[axis] = 1
    prefix = np.concatenate((np.zeros(zero_shape, dtype=np.float64),
                             np.cumsum(values, axis=axis, dtype=np.float64)), axis=axis)
    integral = np.take(prefix, cells, axis=axis)
    integral += np.take(values, np.minimum(cells, original - 1), axis=axis) * fractions.reshape(shape)
    return (np.diff(integral, axis=axis) * (size / original)).astype(np.float32)


def _resize(values, width, height, normal):
    if not normal:
        return _area_axis(_area_axis(values, width, 1), height, 0)
    work = values.copy()
    work[:, :, :3] = work[:, :, :3] * 2 - 1
    work = _area_axis(_area_axis(work, width, 1), height, 0)
    vector = work[:, :, :3]
    length = np.linalg.norm(vector, axis=2, keepdims=True)
    unit = np.divide(vector, length, out=np.zeros_like(vector), where=length > 1e-6)
    unit[:, :, 2] = np.where(length[:, :, 0] > 1e-6, unit[:, :, 2], 1)
    work[:, :, :3] = unit * .5 + .5
    return work


def _encode(values, srgb):
    work = values.copy()
    if srgb:
        rgb = work[:, :, :3]
        work[:, :, :3] = np.where(rgb <= .0031308, rgb * 12.92, 1.055 * rgb ** (1 / 2.4) - .055)
    return np.clip(np.rint(work * 255), 0, 255).astype(np.uint8).tobytes()


def export_texture(image, output, role, maximum_size=MAXIMUM_DIMENSION, maximum_bytes=MAXIMUM_BYTES):
    """Write one new <Image-UUID-N>.siaasset; admission excludes authoring scratch."""
    if image is None or image.source not in ("FILE", "GENERATED") or image.is_float:
        raise ValueError("Select one static byte-buffer Image; float/HDR/sequence/tiled sources require separate authoring")
    identity = image.get("sia.asset_id")
    if not isinstance(identity, str):
        raise ValueError("Image requires an authored sia.asset_id UUID")
    identity = uuid.UUID(identity)
    if identity.int == 0:
        raise ValueError("Image asset UUID must be nonempty")
    if role not in ("color", "data", "normal"):
        raise ValueError("Select an explicit color, data or normal texture role")
    srgb = role == "color"
    if image.colorspace_settings.name != ("sRGB" if srgb else "Non-Color"):
        raise ValueError("Image color space does not match the selected texture role")
    if image.alpha_mode not in ("STRAIGHT", "NONE", "CHANNEL_PACKED") or srgb and image.alpha_mode == "CHANNEL_PACKED":
        raise ValueError("Premultiplied or packed color alpha requires separate authoring")
    if (isinstance(maximum_size, bool) or not isinstance(maximum_size, int) or not 1 <= maximum_size <= MAXIMUM_DIMENSION
            or isinstance(maximum_bytes, bool) or not isinstance(maximum_bytes, int) or not 0 <= maximum_bytes <= MAXIMUM_BYTES):
        raise ValueError("Size must be 1..8192 and byte admission 0..128 MiB")
    width, height = image.size
    if not 1 <= width <= MAXIMUM_DIMENSION or not 1 <= height <= MAXIMUM_DIMENSION or len(image.pixels) != width * height * 4:
        raise ValueError("Image requires 1..8192 dimensions and a complete RGBA buffer")
    scale = min(1.0, maximum_size / max(width, height))
    target_width, target_height = max(1, round(width * scale)), max(1, round(height * scale))
    sizes, payload = [], 0
    w, h = target_width, target_height
    while True:
        sizes.append((w, h))
        payload += w * h * 4
        if w == h == 1:
            break
        w, h = max(1, w // 2), max(1, h // 2)
    if payload + 64 > maximum_bytes or payload + 16 * len(sizes) > maximum_bytes:
        raise ValueError("Texture exceeds encoded or owned-payload admission")
    output = Path(output).absolute()
    if output.name != identity.hex + ".siaasset":
        raise ValueError("Texture filename must be its authored UUID in N format plus .siaasset")
    if output.exists() or output.is_symlink():
        raise FileExistsError("Texture output must not already exist")
    if not output.parent.is_dir():
        raise FileNotFoundError("Texture output requires an existing parent directory")

    pixels = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    values = pixels.reshape(height, width, 4)
    if not np.isfinite(values).all() or np.any(values < 0) or np.any(values > 1):
        raise ValueError("Byte-buffer pixels must be finite normalized values")
    # Byte-buffer pixels preserve encoded RGB; alpha and data channels are linear.
    if image.alpha_mode == "NONE":
        values[:, :, 3] = 1
    if srgb:
        rgb = values[:, :, :3]
        values[:, :, :3] = np.where(rgb <= .04045, rgb / 12.92, ((rgb + .055) / 1.055) ** 2.4)
    if (target_width, target_height) != (width, height):
        values = _resize(values, target_width, target_height, role == "normal")
    levels = []
    for index, (w, h) in enumerate(sizes):
        if index:
            values = _resize(values, w, h, role == "normal")
        levels.append(_encode(values, srgb))
    # Existing WebGPU enum bytes: Repeat=2, Linear=2, UseMipmaps=true.
    header = b"SIATEXR1" + struct.pack("<2I", target_width, target_height)
    header += bytes((int(srgb), len(levels), 2, 2, 2, 2, 2, 1)) + struct.pack("<Q", payload)
    digest = hashlib.sha256(header)
    for level in levels:
        digest.update(level)
    header += digest.digest()
    created = False
    try:
        with output.open("xb") as stream:
            created = True
            stream.write(header)
            for level in levels:
                stream.write(level)
    except BaseException:
        if created:
            output.unlink()
        raise
    return identity


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--role", required=True, choices=("color", "data", "normal"))
    parser.add_argument("--maximum-size", type=int, default=MAXIMUM_DIMENSION)
    parser.add_argument("--maximum-bytes", type=int, default=MAXIMUM_BYTES)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    identity = export_texture(bpy.data.images.get(args.image), args.output, args.role, args.maximum_size, args.maximum_bytes)
    print(f"Texture {identity}: {args.output}")


if __name__ == "__main__":
    main()
