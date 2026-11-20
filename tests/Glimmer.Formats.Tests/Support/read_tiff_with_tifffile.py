# New file, no Swift original: the outside reader for TiffWriterTests and the exporter's tests (tifffile, BSD-3, with
# imagecodecs for the predictors). Prints, as JSON, the tags tifffile reads, the profile's SHA-256, the decoded samples'
# SHA-256 in little-endian order (so a test can compare them with what it wrote) and one pixel's samples.
# Run with python3 -I (isolated); the user's own site-packages, where pip --user installs, is added back by name.
import hashlib
import json
import site
import sys

sys.path.append(site.getusersitepackages())
import tifffile  # noqa: E402

TAGS = (256, 257, 258, 259, 262, 274, 277, 278, 284, 296, 317, 338, 339)


def number(value):
    if isinstance(value, (tuple, list)):
        return [number(v) for v in value]
    try:
        return int(value)
    except (TypeError, ValueError):
        return str(value)


with tifffile.TiffFile(sys.argv[1]) as tif:
    page = tif.pages[0]
    tags = {str(code): number(page.tags[code].value) for code in TAGS if code in page.tags}
    icc = page.tags.get(34675)
    resolution = page.tags.get(282)
    data = page.asarray()
    little = data.astype(data.dtype.newbyteorder("<"), copy=False)
    print(json.dumps({
        "bigtiff": bool(tif.is_bigtiff),
        "byteorder": tif.byteorder,
        "pages": len(tif.pages),
        "tags": tags,
        "xresolution": list(resolution.value) if resolution is not None else None,
        "icc": hashlib.sha256(bytes(icc.value)).hexdigest() if icc is not None else None,
        "shape": list(data.shape),
        "dtype": str(data.dtype),
        "sha": hashlib.sha256(little.tobytes()).hexdigest(),
        # One pixel's samples as numbers (the exporter's tests read values, not hashes).
        "pixel_10_10": [float(v) for v in (data[10, 10] if data.ndim == 3 else [data[10, 10]])]
        if data.ndim >= 2 and data.shape[0] > 10 and data.shape[1] > 10 else None,
    }))
