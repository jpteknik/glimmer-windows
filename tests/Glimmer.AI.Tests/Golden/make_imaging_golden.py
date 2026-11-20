#!/usr/bin/env python3
"""Golden values for the Imaging tests, computed with numpy / scipy / Pillow (independent of the C# code).

The test inputs are formula images, so both sides can build them without sharing data:
  rgb(x, y, c) = 128 + 81 * sin(0.31 x + 0.17 y + 1.7 c)   (float; smooth, so Pillow's 8-bit clamp between passes never bites)
  alpha(x, y)  = 255 for opaque tests, or 40 + (x * 11 + y * 3) % 216 for the translucent one
"""
import json
import sys

import numpy as np
from PIL import Image
from scipy import ndimage


def rgb_image(w, h):
    y, x = np.mgrid[0:h, 0:w]
    out = np.zeros((h, w, 3), dtype=np.float64)
    for c in range(3):
        out[:, :, c] = 128 + 81 * np.sin(0.31 * x + 0.17 * y + 1.7 * c)
    return out


def alpha_image(w, h):
    y, x = np.mgrid[0:h, 0:w]
    return (40 + (x * 11 + y * 3) % 216).astype(np.float64)


def srgb_to_lab(rgb):  # rgb 0..255 float
    c = rgb / 255.0
    lin = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    m = np.array([[0.4124564, 0.3575761, 0.1804375],
                  [0.2126729, 0.7151522, 0.0721750],
                  [0.0193339, 0.1191920, 0.9503041]])
    xyz = lin @ m.T
    xyz = xyz / np.array([0.95047, 1.0, 1.08883])
    f = np.where(xyz > 0.008856, np.cbrt(xyz), 7.787 * xyz + 16.0 / 116)
    L = 116 * f[..., 1] - 16
    a = 500 * (f[..., 0] - f[..., 1])
    b = 200 * (f[..., 1] - f[..., 2])
    return np.stack([L, a, b], axis=-1)


def lab_to_srgb(lab):
    fy = (lab[..., 0] + 16) / 116
    fx = fy + lab[..., 1] / 500
    fz = fy - lab[..., 2] / 200
    f = np.stack([fx, fy, fz], axis=-1)
    inv = np.where(f ** 3 > 0.008856, f ** 3, (f - 16.0 / 116) / 7.787)
    xyz = inv * np.array([0.95047, 1.0, 1.08883])
    m = np.array([[3.2404542, -1.5371385, -0.4985314],
                  [-0.9692660, 1.8760108, 0.0415560],
                  [0.0556434, -0.2040259, 1.0572252]])
    lin = np.clip(xyz @ m.T, 0, 1)
    enc = np.where(lin <= 0.0031308, lin * 12.92, 1.055 * lin ** (1 / 2.4) - 0.055)
    return enc * 255


def lab_transfer(src, ref, strength):
    ls, lr = srgb_to_lab(src), srgb_to_lab(ref)
    ms, ss = ls.reshape(-1, 3).mean(0), ls.reshape(-1, 3).std(0)
    mr, sr = lr.reshape(-1, 3).mean(0), lr.reshape(-1, 3).std(0)
    ss = np.where(ss < 1e-6, 1, ss)
    sr = np.where(sr < 1e-6, 1, sr)
    t = (ls - ms) / ss * sr + mr
    rgb = lab_to_srgb(t)
    return src + (rgb - src) * strength


def gaussian_kernel(sigma):
    radius = max(1, int(np.ceil(sigma * 3)))
    k = np.exp(-(np.arange(-radius, radius + 1) ** 2) / (2 * sigma * sigma))
    return k / k.sum()


def feather(mask, erode, blur):
    v = mask.astype(np.float64)
    if erode > 0:
        k = max(3, erode * 2 + 1)
        v = ndimage.minimum_filter(v, size=k, mode="nearest")
    if blur > 0:
        k = gaussian_kernel(blur)
        v = ndimage.convolve1d(v, k, axis=1, mode="nearest")
        v = ndimage.convolve1d(v, k, axis=0, mode="nearest")
    return v


def fit(src, ref, w):
    sw = w.sum(); sx = (w * src).sum(); sy = (w * ref).sum()
    sxx = (w * src * src).sum(); sxy = (w * src * ref).sum()
    if sw < 1:
        return 1.0, 0.0
    d = sw * sxx - sx * sx
    if abs(d) < 1e-6:
        return 1.0, float(np.clip((sy - sx) / sw, -64, 64))
    g = (sw * sxy - sx * sy) / d
    o = (sy - g * sx) / sw
    return float(np.clip(g, 0.5, 2)), float(np.clip(o, -64, 64))


def color_match(res, ref, trusted):
    w = trusted / 255.0
    out = np.zeros_like(res)
    gains = []
    for c in range(3):
        g, o = fit(res[..., c].ravel(), ref[..., c].ravel(), w.ravel())
        gains.append([g, o])
        out[..., c] = np.trunc(np.clip(res[..., c] * g + o, 0, 255))
    return out, gains


def main():
    out = {}

    # --- Lanczos resize against Pillow (Pillow resizes RGBA premultiplied, like the original's context).
    for name, (sw, sh, dw, dh, translucent) in {
        "up": (23, 17, 61, 40, False),
        "down": (97, 71, 31, 22, False),
        "down_translucent": (64, 48, 25, 19, True),
    }.items():
        rgb = rgb_image(sw, sh)
        a = alpha_image(sw, sh) if translucent else np.full((sh, sw), 255.0)
        img = np.dstack([rgb, a]).round().clip(0, 255).astype(np.uint8)
        # Float resize per premultiplied channel (mode "F": no 8-bit rounding between the passes).
        inp = img.astype(np.float64)
        pre = [inp[..., c] * inp[..., 3] / 255.0 for c in range(3)] + [inp[..., 3]]
        res = [np.asarray(Image.fromarray(ch.astype(np.float32), "F").resize((dw, dh), Image.LANCZOS)).astype(np.float64) for ch in pre]
        out["resize_" + name] = {"sw": sw, "sh": sh, "dw": dw, "dh": dh, "translucent": translucent,
                                 "input": img.astype(int).ravel().tolist(),
                                 "premultiplied": np.stack(res, axis=-1).ravel().tolist()}

    # --- Feather against scipy.
    w, h = 40, 30
    mask = np.zeros((h, w))
    mask[8:22, 10:31] = 255
    mask[3, 3] = 255
    out["feather"] = {"w": w, "h": h, "erode": 2, "blur": 3.0,
                      "values": feather(mask, 2, 3.0).ravel().tolist()}
    out["feather_blur_only"] = {"w": w, "h": h, "erode": 0, "blur": 1.5,
                                "values": feather(mask, 0, 1.5).ravel().tolist()}
    out["feather_erode_only"] = {"w": w, "h": h, "erode": 3, "blur": 0.0,
                                 "values": feather(mask, 3, 0).ravel().tolist()}

    # --- Lab transfer.
    sw_, sh_ = 17, 11
    src = rgb_image(sw_, sh_)
    ref = np.zeros((9, 13, 3))
    yy, xx = np.mgrid[0:9, 0:13]
    ref[..., 0] = 140 + xx * 5
    ref[..., 1] = 90 + yy * 6
    ref[..., 2] = 60 + (xx + yy) * 3
    out["lab"] = {"sw": sw_, "sh": sh_, "strength": 0.8, "values": lab_transfer(src, ref, 0.8).ravel().tolist()}

    # --- Colour match with a partial trust mask.
    cw, ch = 24, 18
    refc = rgb_image(cw, ch)
    resc = np.clip((refc - 14) / 1.15 + 3, 0, 255).round()
    trusted = np.zeros((ch, cw))
    trusted[:, :14] = 255
    trusted[5:9, 14:20] = 128
    cm, gains = color_match(resc, refc, trusted)
    out["color_match"] = {"w": cw, "h": ch, "gains": gains, "values": cm.ravel().tolist(),
                          "result": resc.ravel().tolist(), "reference": refc.ravel().tolist(),
                          "trusted": trusted.ravel().tolist()}

    # Gain clamps: the wanted gain is 0.33 (clamped up to 0.5) and 2.6 (clamped down to 2); the offset comes from the unclamped fit.
    for name, g, o in (("color_match_low_gain", 0.33, 50.0), ("color_match_high_gain", 2.6, -150.0)):
        resq = rgb_image(cw, ch).round()
        refq = np.clip(resq * g + o, 0, 255).round() if g < 1 else np.clip(resq * g + o, 0, 255).round()
        full = np.full((ch, cw), 255.0)
        cmq, gq = color_match(resq, refq, full)
        out[name] = {"w": cw, "h": ch, "gains": gq, "values": cmq.ravel().tolist(),
                     "result": resq.ravel().tolist(), "reference": refq.ravel().tolist(), "trusted": full.ravel().tolist()}

    json.dump(out, sys.stdout)


if __name__ == "__main__":
    main()
