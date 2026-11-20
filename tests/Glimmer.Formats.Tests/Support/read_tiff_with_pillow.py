# New file, no Swift original: a second outside reader for TiffWriterTests. Pillow decodes TIFF through libtiff, a
# different implementation from tifffile. Prints the mode, size, profile and the SHA-256 of the samples as Pillow
# holds them (8-bit modes only; for others the samples are left out).
# Run with python3 -I (isolated); the user's own site-packages, where pip --user installs, is added back by name.
import hashlib
import json
import site
import sys

sys.path.append(site.getusersitepackages())
from PIL import Image  # noqa: E402

with Image.open(sys.argv[1]) as image:
    image.load()
    icc = image.info.get("icc_profile")
    print(json.dumps({
        "format": image.format,
        "mode": image.mode,
        "width": image.width,
        "height": image.height,
        "icc": hashlib.sha256(icc).hexdigest() if icc else None,
        "sha": hashlib.sha256(image.tobytes()).hexdigest() if image.mode in ("RGB", "RGBA", "RGBa") else None,
    }))
