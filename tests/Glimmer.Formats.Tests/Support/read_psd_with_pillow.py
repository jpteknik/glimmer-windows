# New file, no Swift original: the outside reader for PSDWriterTests (the Swift test uses macOS's ImageIO).
# Prints what Pillow, an independent PSD reader, makes of a file's flattened picture, as JSON.
# Run with python3 -I (isolated); the user's own site-packages, where pip --user installs, is added back by name.
import base64
import json
import site
import sys

sys.path.append(site.getusersitepackages())
from PIL import Image  # noqa: E402

with Image.open(sys.argv[1]) as image:
    image.load()
    print(json.dumps({
        "format": image.format,
        "mode": image.mode,
        "width": image.width,
        "height": image.height,
        "layers": len(getattr(image, "layers", []) or []),
        "pixels": base64.b64encode(image.convert("RGB").tobytes()).decode("ascii"),
    }))
