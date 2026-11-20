# New file, no Swift original: an outside reader for the PSD tests (psd-tools, MIT, independent of Glimmer).
# For each file named, prints one JSON line with what psd-tools reads at the level of PSDFile: the header, every
# layer record and the SHA-256 of every decoded channel, and the flattened picture's channels. Masks are decoded
# twice, at 8 bits and at the document's depth (the Mac's reader and Photoshop differ on that), and the one at the
# document's depth also brought down to one byte a pixel by the rule Glimmer's reader states (docs/UPSTREAM-NOTES.md,
# "PSD reader", item 1): 16-bit levels to the nearest of 256 steps, 32-bit values clamped to 0...1 and rounded.
# Run with python3 -I (isolated); the user's own site-packages, where pip --user installs, is added back by name.
import hashlib
import json
import logging
import math
import site
import struct
import sys

sys.path.append(site.getusersitepackages())
logging.disable(logging.CRITICAL)
from psd_tools.psd import PSD  # noqa: E402


def sha(data):
    return hashlib.sha256(bytes(data)).hexdigest()


def get(channel, width, height, depth, version):
    """The decoded bytes, or the name of psd-tools' own refusal (a result too)."""
    try:
        return bytes(channel.get_data(width, height, depth, version)), None
    except Exception as error:  # noqa: BLE001
        return None, type(error).__name__


def decode(channel, width, height, depth, version):
    if width <= 0 or height <= 0:
        return None
    data, error = get(channel, width, height, depth, version)
    return {"error": error} if data is None else {"sha": sha(data), "length": len(data)}


def reduced(data, depth):
    """A mask read at the document's depth, one byte a pixel."""
    if depth == 16:
        values = struct.unpack(">%dH" % (len(data) // 2), data[: len(data) // 2 * 2])
        return bytes((v * 255 + 32767) // 65535 for v in values)
    if depth == 32:
        out = bytearray()
        for f in struct.unpack(">%df" % (len(data) // 4), data[: len(data) // 4 * 4]):
            if f != f:
                out.append(0)
                continue
            # The product in single precision, as the reader computes it, then halves away from zero.
            x = struct.unpack(">f", struct.pack(">f", min(max(f, 0.0), 1.0) * 255.0))[0]
            out.append(int(math.floor(x + 0.5)))
        return bytes(out)
    return data


def key_text(key):
    value = getattr(key, "value", key)
    return value.decode("latin-1") if isinstance(value, bytes) else str(value)


def dump(path):
    out = {"file": path}
    try:
        with open(path, "rb") as f:
            psd = PSD.read(f)
    except Exception as error:  # noqa: BLE001
        out["error"] = f"{type(error).__name__}: {error}"[:200]
        return out
    h = psd.header
    out["header"] = {"version": h.version, "channels": h.channels, "width": h.width, "height": h.height,
                     "depth": h.depth, "mode": int(h.color_mode)}
    layers = []
    info = psd._get_layer_info()
    if info is not None and info.layer_records is not None:
        for record, channels in zip(info.layer_records, info.channel_image_data or []):
            width, height = record.right - record.left, record.bottom - record.top
            mask = record.mask_data
            layer = {
                "rect": [record.top, record.left, record.bottom, record.right],
                "blend": key_text(record.blend_mode),
                "opacity": record.opacity,
                "clipping": int(record.clipping),
                "visible": bool(record.flags.visible),
                "keys": [key_text(k) for k in (record.tagged_blocks.keys() if record.tagged_blocks is not None else [])],
                "channels": [],
            }
            if mask is not None:
                layer["mask"] = {"rect": [mask.top, mask.left, mask.bottom, mask.right], "color": mask.background_color,
                                 "real": None if mask.real_top is None else [mask.real_top, mask.real_left, mask.real_bottom, mask.real_right]}
            for info_entry, channel in zip(record.channel_info, channels):
                entry = {"id": int(info_entry.id), "compression": int(channel.compression)}
                if info_entry.id >= -1:
                    entry["data"] = decode(channel, width, height, h.depth, h.version)
                elif mask is not None:
                    if info_entry.id == -3 and mask.real_top is not None:
                        mw, mh = mask.real_right - mask.real_left, mask.real_bottom - mask.real_top
                    else:
                        mw, mh = mask.right - mask.left, mask.bottom - mask.top
                    entry["data8"] = decode(channel, mw, mh, 8, h.version)
                    entry["dataDepth"] = decode(channel, mw, mh, h.depth, h.version)
                    if mw > 0 and mh > 0:
                        data, error = get(channel, mw, mh, h.depth, h.version)
                        entry["dataReduced"] = {"error": error} if data is None else {"sha": sha(reduced(data, h.depth))}
                layer["channels"].append(entry)
            layers.append(layer)
    out["layers"] = layers
    try:
        planes = psd.image_data.get_data(h, True)
        out["composite"] = [sha(p) for p in planes]
    except Exception as error:  # noqa: BLE001
        out["composite_error"] = type(error).__name__
    return out


for name in sys.argv[1:]:
    print(json.dumps(dump(name)))
    sys.stdout.flush()
