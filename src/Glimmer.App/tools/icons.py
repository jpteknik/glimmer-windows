#!/usr/bin/env python3
"""The icon catalogue: every SF Symbol name the original's views use, and what draws it on Windows.

    python3 -I tools/icons.py <project dir>

Writes three files from the one table below, so they cannot disagree:
  Icons/IconCatalog.g.cs          the catalogue the Icon control reads (SF name -> glyph, path or text)
  Icons/SF-Symbols-to-Windows.md  the same table for people
  tools/out/icons-preview.html    every path icon drawn at 16, 24 and 48 px, to look at in a browser

A glyph is a code point in Segoe Fluent Icons (Windows 11), which Segoe MDL2 Assets (Windows 10) also has for
everything below F000; the code points are from Microsoft's published table of the font. A path is drawn here in a
16 x 16 box, in the path syntax WPF and SVG share (M L H V C Q A Z), stroked 1.3 units wide with round ends unless it
is a fill. A text is drawn in the interface font (the original's "textformat" shows the letters Aa).
"""
import html
import math
import os
import sys

# ---------------------------------------------------------------------------------------------------------- helpers


def f(v: float) -> str:
    s = f"{v:.2f}".rstrip("0").rstrip(".")
    return "0" if s == "-0" else s


def pt(x, y):
    return f"{f(x)},{f(y)}"


def circle(cx, cy, r):
    return f"M{pt(cx - r, cy)} A{f(r)},{f(r)} 0 1 0 {pt(cx + r, cy)} A{f(r)},{f(r)} 0 1 0 {pt(cx - r, cy)} Z"


def rrect(x0, y0, x1, y1, r):
    return (f"M{pt(x0 + r, y0)} H{f(x1 - r)} A{f(r)},{f(r)} 0 0 1 {pt(x1, y0 + r)} V{f(y1 - r)} "
            f"A{f(r)},{f(r)} 0 0 1 {pt(x1 - r, y1)} H{f(x0 + r)} A{f(r)},{f(r)} 0 0 1 {pt(x0, y1 - r)} "
            f"V{f(y0 + r)} A{f(r)},{f(r)} 0 0 1 {pt(x0 + r, y0)} Z")


def poly(points, close=True):
    head, *rest = points
    s = f"M{pt(*head)} " + " ".join(f"L{pt(*p)}" for p in rest)
    return s + (" Z" if close else "")


def star4(cx, cy, r, k=0.2):
    """A four-pointed sparkle: points on the axes, sides bowed out by k."""
    c = r * k
    return (f"M{pt(cx, cy - r)} Q{pt(cx + c, cy - c)} {pt(cx + r, cy)} Q{pt(cx + c, cy + c)} {pt(cx, cy + r)} "
            f"Q{pt(cx - c, cy + c)} {pt(cx - r, cy)} Q{pt(cx - c, cy - c)} {pt(cx, cy - r)} Z")


def dashed_line(x0, y0, x1, y1, dash, gap):
    length = math.hypot(x1 - x0, y1 - y0)
    ux, uy = (x1 - x0) / length, (y1 - y0) / length
    out, t = [], 0.0
    while t < length - 0.01:
        e = min(t + dash, length)
        out.append(f"M{pt(x0 + ux * t, y0 + uy * t)} L{pt(x0 + ux * e, y0 + uy * e)}")
        t = e + gap
    return " ".join(out)


def dashed_rect(x0, y0, x1, y1):
    # Corners first, so every corner reads as a corner; the sides get centred dashes between them.
    c = 2.2
    parts = [f"M{pt(x0, y0 + c)} V{f(y0)} H{f(x0 + c)}", f"M{pt(x1 - c, y0)} H{f(x1)} V{f(y0 + c)}",
             f"M{pt(x1, y1 - c)} V{f(y1)} H{f(x1 - c)}", f"M{pt(x0 + c, y1)} H{f(x0)} V{f(y1 - c)}"]
    mx, my = (x0 + x1) / 2, (y0 + y1) / 2
    parts += [f"M{pt(mx - 1.3, y0)} H{f(mx + 1.3)}", f"M{pt(mx - 1.3, y1)} H{f(mx + 1.3)}",
              f"M{pt(x0, my - 1.2)} V{f(my + 1.2)}", f"M{pt(x1, my - 1.2)} V{f(my + 1.2)}"]
    return " ".join(parts)


def dashed_circle(cx, cy, r, n=8, fill=0.55):
    out = []
    for i in range(n):
        a0 = (i / n) * 2 * math.pi - math.pi / 2
        a1 = a0 + (2 * math.pi / n) * fill
        out.append(f"M{pt(cx + r * math.cos(a0), cy + r * math.sin(a0))} "
                   f"A{f(r)},{f(r)} 0 0 1 {pt(cx + r * math.cos(a1), cy + r * math.sin(a1))}")
    return " ".join(out)


def rotate(points, deg, cx=8, cy=8):
    a = math.radians(deg)
    return [(cx + (x - cx) * math.cos(a) - (y - cy) * math.sin(a), cy + (x - cx) * math.sin(a) + (y - cy) * math.cos(a))
            for x, y in points]


def thick(polyline, w):
    """The outline of a polyline drawn w wide (mitred joins), as one closed polygon: a mark knocked out of a fill."""
    def normal(p, q):
        dx, dy = q[0] - p[0], q[1] - p[1]
        n = math.hypot(dx, dy)
        return -dy / n, dx / n
    left, right, h = [], [], w / 2
    for i, p in enumerate(polyline):
        ns = []
        if i > 0:
            ns.append(normal(polyline[i - 1], p))
        if i < len(polyline) - 1:
            ns.append(normal(p, polyline[i + 1]))
        nx, ny = sum(n[0] for n in ns), sum(n[1] for n in ns)
        m = math.hypot(nx, ny)
        nx, ny = nx / m, ny / m
        scale = h / max(0.35, abs(nx * ns[0][0] + ny * ns[0][1]))
        left.append((p[0] + nx * scale, p[1] + ny * scale))
        right.append((p[0] - nx * scale, p[1] - ny * scale))
    return poly(left + right[::-1])


def x_shape(cx, cy, arm, w):
    """An X as one polygon (no overlap, so it can be knocked out of a fill with the even-odd rule)."""
    h = w / math.sqrt(2)
    pts = []
    for k in range(4):
        a = math.radians(45 + 90 * k)
        ux, uy = math.cos(a), math.sin(a)
        vx, vy = -uy, ux
        pts.append((cx + vx * h * 1.0 + 0, cy + vy * h))  # inner corner between arms
        pts.append((cx + ux * arm + vx * w / 2, cy + uy * arm + vy * w / 2))
        pts.append((cx + ux * arm - vx * w / 2, cy + uy * arm - vy * w / 2))
    # Order: inner corner, then the tip of the next arm; rebuild so corners and tips alternate around the centre.
    out = []
    for k in range(4):
        a = math.radians(45 + 90 * k)
        ux, uy = math.cos(a), math.sin(a)
        vx, vy = -uy, ux
        out.append((cx + ux * arm - vx * w / 2, cy + uy * arm - vy * w / 2))
        out.append((cx + ux * arm + vx * w / 2, cy + uy * arm + vy * w / 2))
        b = math.radians(45 + 90 * k + 45)
        out.append((cx + math.cos(b) * h, cy + math.sin(b) * h))
    return poly(out)


def star5(cx, cy, r_out, r_in):
    return [(cx + (r_out if i % 2 == 0 else r_in) * math.sin(math.radians(i * 36)),
             cy - (r_out if i % 2 == 0 else r_in) * math.cos(math.radians(i * 36))) for i in range(10)]


def clip_left(points, x_max):
    """Sutherland-Hodgman clip of a polygon to x <= x_max."""
    out = []
    for i, p in enumerate(points):
        q = points[(i + 1) % len(points)]
        pin, qin = p[0] <= x_max, q[0] <= x_max
        if pin:
            out.append(p)
        if pin != qin:
            t = (x_max - p[0]) / (q[0] - p[0])
            out.append((x_max, p[1] + t * (q[1] - p[1])))
    return out


def seal(cx, cy, r, bumps=12, depth=0.9):
    pts = []
    for i in range(bumps * 2):
        a = math.pi * i / bumps
        rr = r if i % 2 == 0 else r - depth
        pts.append((cx + rr * math.sin(a), cy - rr * math.cos(a)))
    return poly(pts)


def aperture(cx, cy, r):
    parts = [circle(cx, cy, r)]
    for i in range(6):
        a = math.radians(i * 60 - 90)
        b = math.radians(i * 60 - 90 + 60)
        p = (cx + 2.6 * math.cos(a), cy + 2.6 * math.sin(a))
        q = (cx + r * math.cos(b + 0.35), cy + r * math.sin(b + 0.35))
        parts.append(f"M{pt(*p)} L{pt(*q)}")
    return " ".join(parts)


DROP = "M8,1.8 C8,1.8 3.2,7.4 3.2,10.3 A4.8,4.8 0 0 0 12.8,10.3 C12.8,7.4 8,1.8 8,1.8 Z"
PERSON = "M8,3.6 A2.3,2.3 0 1 0 8,8.2 A2.3,2.3 0 1 0 8,3.6 Z M3.8,13.6 C3.8,11 5.6,9.7 8,9.7 C10.4,9.7 12.2,11 12.2,13.6"

# ------------------------------------------------------------------------------------------------------------ table
# sf name: (kind, value, windows name or note, where the original uses it)
#   ("glyph", "E7C2")                         Segoe Fluent Icons / Segoe MDL2 Assets code point
#   ("glyph", "E7AD", {"mirror": True})        the glyph mirrored left-right
#   ("path", {"stroke": ..., "fill": ..., "evenodd": bool})
#   ("text", "Aa", {"italic": bool})

ICONS = {
    # Tool rail (DocumentView.swift, ToolID.symbolName), in the rail's order.
    "arrow.up.and.down.and.arrow.left.and.right": ("glyph", "E7C2", "Move", "Move tool"),
    "rectangle.dashed": ("path", {"stroke": dashed_rect(2.2, 3.6, 13.8, 12.4)}, "dashed rectangle", "Rectangular Marquee; Editing mask; Layer Mask"),
    "circle.dashed": ("path", {"stroke": dashed_circle(8, 8, 5.9, n=10, fill=0.6)}, "dashed circle", "Elliptical Marquee"),
    "lasso": ("path", {"stroke": "M5.3,10.4 C2.6,9.6 1.6,7.9 2.3,6.2 C3.2,3.8 6.6,2.5 9.9,3 C13.1,3.5 14.9,5.6 14.2,7.8 "
                                 "C13.5,9.9 10.6,11.2 7.6,10.9 C6.2,10.8 5.2,11.4 5.3,12.4 C5.4,13.6 6.6,14.3 7.6,14.1"},
              "lasso loop", "Lasso"),
    "skew": ("path", {"stroke": poly([(5.4, 3.2), (14.2, 3.2), (10.6, 12.8), (1.8, 12.8)])}, "parallelogram", "Polygonal Lasso"),
    "wand.and.stars": ("path", {"stroke": "M2.4,13.6 L9.3,6.7",
                                "fill": star4(11.9, 4.1, 2.5) + " " + star4(13.8, 9.2, 1.3) + " " + star4(6.6, 2.6, 1.3)},
                       "wand with stars", "Magic Wand"),
    "wand.and.sparkles": ("path", {"stroke": "M2.4,13.6 L8.6,7.4",
                                   "fill": star4(11.6, 4.4, 3.4, 0.22) + " " + star4(6, 2.4, 1.3) + " " + star4(13.9, 10.2, 1.3)},
                          "wand with sparkles", "Magic Select; Apply Magic Select; omnibar suggestions"),
    "crop": ("glyph", "E7A8", "Crop", "Crop tool; Suggest Crop"),
    "arrow.up.left.and.down.right.and.arrow.up.right.and.down.left": (
        "path", {"stroke": "M3,3 L13,13 M13,3 L3,13 M3,7 V3 H7 M9,3 H13 V7 M13,9 V13 H9 M7,13 H3 V9"},
        "corner arrows", "Free Transform"),
    "paintbrush.pointed": ("path", {"stroke": "M14,2 L8.4,8.6",
                                    "fill": "M1.8,14.2 C2.1,11.6 3.4,9.9 5.6,9.6 L7.4,11.4 C7.1,13.6 5,14.6 1.8,14.2 Z "
                                            "M6.3,8.9 L8.1,7.4 L9.6,8.9 L8.1,10.7 Z"},
                           "pointed brush", "Brush; Generative Fill; Stroke path"),
    "eraser": ("glyph", "E75C", "EraseTool", "Eraser; Remove (selection bar)"),
    "drop.halffull": ("path", {"stroke": DROP, "fill": "M3.2,10.3 A4.8,4.8 0 0 0 12.8,10.3 Z"},
                      "half-filled drop", "Paint Bucket; Blur"),
    "square.bottomhalf.filled": ("path", {"stroke": rrect(2.4, 2.4, 13.6, 13.6, 2),
                                          "fill": "M2.4,8 H13.6 V11.6 A2,2 0 0 1 11.6,13.6 H4.4 A2,2 0 0 1 2.4,11.6 Z"},
                                 "square, lower half filled", "Gradient"),
    "seal": ("path", {"stroke": seal(8, 8, 6.4)}, "scalloped seal", "Clone Stamp"),
    "bandage": ("path", {"stroke": poly(rotate([(1.6, 5.6), (14.4, 5.6), (14.4, 10.4), (1.6, 10.4)], -45)) + " " +
                                   poly(rotate([(6.2, 5.6), (9.8, 5.6), (9.8, 10.4), (6.2, 10.4)], -45))},
                "bandage", "Healing Brush; Heal (selection bar)"),
    "square.grid.3x3.square": ("path", {"stroke": rrect(2.2, 2.2, 13.8, 13.8, 1.8) + " M6.07,2.2 V13.8 M9.93,2.2 V13.8 M2.2,6.07 H13.8 M2.2,9.93 H13.8"},
                               "grid in a square", "Pattern Stamp"),
    "circle.lefthalf.filled": ("path", {"stroke": circle(8, 8, 6), "fill": "M8,2 A6,6 0 0 0 8,14 Z"},
                               "circle, left half filled", "Dodge; Reset Colors; adjustment/fill menu; Black & White"),
    "flame": ("path", {"stroke": "M8.2,1.6 C8.6,4.4 12.6,6 12.6,10 A4.6,4.6 0 0 1 3.4,10 C3.4,7.6 4.6,6.4 5.4,4.9 "
                                 "C6,6.3 6.8,7 7.6,7 C7.4,5.2 7.6,3.4 8.2,1.6 Z"}, "flame", "Burn"),
    "triangle": ("path", {"stroke": poly([(8, 2.3), (14.2, 13.2), (1.8, 13.2)])}, "triangle", "Sharpen"),
    "hand.point.up": ("path", {"stroke": "M6.2,9.6 V2.9 A1.25,1.25 0 0 1 8.7,2.9 V7.4 V6.9 A1.2,1.2 0 0 1 11.1,6.9 V7.8 "
                                         "A1.2,1.2 0 0 1 13.5,7.8 V10.6 C13.5,13.1 12,14.6 9.6,14.6 H8.7 "
                                         "C6.7,14.6 5.7,13.7 4.7,12.2 L3.1,9.8 A1.1,1.1 0 0 1 4.9,8.6 L6.2,10"},
                      "pointing finger", "Smudge"),
    "textformat": ("text", "Aa", "the letters Aa", "Type tool; text layers"),
    "pencil.tip.crop.circle": ("path", {"stroke": circle(8, 8, 6.4) + " " + poly([(8, 3.8), (10.6, 9.4), (8, 11.6), (5.4, 9.4)]) + " M8,7.4 V9.2"},
                               "pen nib in a circle", "Pen tool; No Paths"),
    "square.on.circle": ("path", {"stroke": rrect(1.8, 2.2, 10, 10.4, 1.6) + " M10,6.2 A4.4,4.4 0 1 1 6,10.4"},
                         "square over a circle", "Shape tool; Shape Path"),
    "eyedropper": ("glyph", "EF3C", "Eyedropper", "Eyedropper"),
    "hand.raised": ("path", {"stroke": "M5,9.4 V4.4 A1,1 0 0 1 7,4.4 V8 M7,8 V3 A1,1 0 0 1 9,3 V8 M9,8 V3.8 A1,1 0 0 1 11,3.8 V8.4 "
                                       "M11,8.4 V5.6 A1,1 0 0 1 13,5.6 V10.2 C13,13 11.5,14.6 8.6,14.6 C6.6,14.6 5.6,13.9 4.4,12.3 "
                                       "L2.6,9.9 A1.1,1.1 0 0 1 4.4,8.7 L5,9.4"},
                    "open hand", "Hand tool"),

    # Chrome (DocumentView.swift, Theme.swift)
    "sparkles": ("path", {"fill": star4(6.4, 9.6, 5.4, 0.22) + " " + star4(12.6, 3.7, 2.7, 0.22) + " " + star4(3.4, 2.9, 1.4, 0.22)},
                 "three sparkles", "AI chip; omnibar; assistant; Suggest"),
    "sparkle": ("path", {"fill": star4(8, 8, 6.2, 0.22)}, "one sparkle", "Title chip"),
    "wrench.and.screwdriver": ("glyph", "E90F", "Repair", "Tools chip"),
    "square.3.layers.3d": ("glyph", "E81E", "MapLayers", "Layers chip"),
    "square.3.layers.3d.slash": ("glyph", "E81E", "MapLayers", "No Layers"),
    "square.and.arrow.down": ("glyph", "E896", "Download", "Export chip"),
    "command": ("path", {"stroke": "M6,6 V10 M10,6 V10 M6,6 H10 M6,10 H10 M6,6 H4.6 A1.6,1.6 0 1 1 6,4.6 V6 "
                                   "M10,6 V4.6 A1.6,1.6 0 1 1 11.4,6 H10 M10,10 H11.4 A1.6,1.6 0 1 1 10,11.4 V10 "
                                   "M6,10 V11.4 A1.6,1.6 0 1 1 4.6,10 H6"},
                "command sign", "Command Palette chip"),
    "xmark": ("glyph", "E711", "Cancel", "Close panel; Deselect; Dismiss"),
    "arrow.left.arrow.right": ("path", {"stroke": "M2.4,5.4 H13.2 M10.4,2.8 L13.2,5.4 L10.4,8 M13.6,10.6 H2.8 M5.6,8 L2.8,10.6 L5.6,13.2"},
                               "two arrows", "Swap Colors"),
    "swatchpalette": ("glyph", "E790", "Color", "Swatches"),
    "link": ("glyph", "E71B", "Link", "Linked image in the title chip"),

    # Layers, Paths, Channels, History (LayersPanel.swift, PathsPanel.swift, ChannelsPanel.swift, HistoryPanel.swift)
    "eye": ("glyph", "E890", "View", "Hide layer / channel"),
    "eye.slash": ("glyph", "ED1A", "Hide", "Show layer / channel"),
    "chevron.right": ("glyph", "E76C", "ChevronRight", "Group disclosure"),
    "arrow.turn.left.down": ("path", {"stroke": "M13,2.6 V6 A2,2 0 0 1 11,8 H5.4 V13 M2.9,10.5 L5.4,13 L7.9,10.5"},
                             "turned arrow", "Clipped to the layer below"),
    "folder": ("glyph", "E8B7", "Folder", "Group layer"),
    "doc.badge.gearshape": ("glyph", "E713", "Settings", "Smart object badge"),
    "square.grid.3x3": ("path", {"stroke": rrect(2.2, 2.2, 13.8, 13.8, 1) + " M6.07,2.2 V13.8 M9.93,2.2 V13.8 M2.2,6.07 H13.8 M2.2,9.93 H13.8"},
                        "grid", "Pattern fill layer"),
    "arrow.up": ("glyph", "E74A", "Up", "Bring Forward"),
    "arrow.down": ("glyph", "E74B", "Down", "Send Backward"),
    "fx": ("text", "fx", "italic fx", "Layer Style button", {"italic": True}),
    "paintbrush.fill": ("path", {"fill": "M14,2 L8.9,8.1 L7.9,7.1 Z M1.8,14.2 C2.1,11.6 3.4,9.9 5.6,9.6 L7.4,11.4 C7.1,13.6 5,14.6 1.8,14.2 Z "
                                         "M6.3,8.9 L8.1,7.4 L9.6,8.9 L8.1,10.7 Z"}, "filled brush", "Fill path"),
    "character.cursor.ibeam": ("path", {"stroke": "M11.6,2.6 V13.4 M10,2.6 H13.2 M10,13.4 H13.2 M1.8,13 L5,4 L8.2,13 M3,9.8 H7"},
                               "letter A and a text cursor", "Type on Path"),
    "trash": ("glyph", "E74D", "Delete", "Delete path"),
    "point.topleft.down.to.point.bottomright.curvepath": (
        "path", {"stroke": "M3.6,3.6 C9.6,3.6 6.4,12.4 12.4,12.4", "fill": rrect(1.6, 1.6, 4.6, 4.6, 0.6) + " " + rrect(11.4, 11.4, 14.4, 14.4, 0.6)},
        "curve with end points", "Path rows; Curves adjustment"),
    "circle.grid.cross": ("path", {"stroke": circle(8, 8, 6) + " M2,8 H14 M8,2 V14"}, "circle with a cross", "RGB channel row"),
    "circle.fill": ("glyph", "EA3B", "CircleFill", "Red, green, blue and alpha channel rows"),
    "selection.pin.in.out": ("glyph", "E8B3", "SelectAll", "Load as Selection; Applies to the selection"),
    "doc": ("glyph", "E8A5", "Document", "History: Open"),
    "arrow.uturn.backward.circle": ("glyph", "E7A7", "Undo", "History steps"),
    "pencil": ("glyph", "E70F", "Edit", "Editing a shape path"),

    # Selection bar, options bar, navigator (SelectionBar.swift, ToolOptionsBar.swift, NavigatorView.swift)
    "circle.righthalf.filled": ("path", {"stroke": circle(8, 8, 6), "fill": "M8,2 A6,6 0 0 1 8,14 Z"},
                                "circle, right half filled", "Invert selection"),
    "circle.dotted": ("path", {"stroke": dashed_circle(8, 8, 5.9, n=12, fill=0.32)}, "dotted circle", "Feather selection"),
    "level": ("path", {"stroke": rrect(1.4, 5.2, 14.6, 10.8, 2.2) + " M6,5.2 V10.8 M10,5.2 V10.8",
                       "fill": circle(8, 8, 1.1)}, "spirit level", "Crop: Level; Auto Straighten"),
    "text.alignleft": ("glyph", "E8E4", "AlignLeft", "Text alignment"),
    "text.aligncenter": ("glyph", "E8E3", "AlignCenter", "Text alignment"),
    "text.alignright": ("glyph", "E8E2", "AlignRight", "Text alignment"),
    "textformat.size.larger": ("glyph", "E8E9", "FontSize", "Warp Text"),
    "minus.magnifyingglass": ("glyph", "E71F", "ZoomOut", "Navigator: Zoom Out"),
    "plus.magnifyingglass": ("glyph", "E8A3", "ZoomIn", "Navigator: Zoom In"),
    "rotate.left": ("glyph", "E7AD", "Rotate (mirrored)", "Navigator: rotate the view left", {"mirror": True}),
    "rotate.right": ("glyph", "E7AD", "Rotate", "Navigator: rotate the view right"),
    "plus": ("glyph", "E710", "Add", "Add Foreground swatch; Add Stop"),
    "minus.circle": ("path", {"stroke": circle(8, 8, 6) + " M5,8 H11"}, "circle with a minus", "Remove stop; skipped plan step"),

    # States (StatusKind.swift): told apart by shape, never by colour alone.
    "xmark.square.fill": ("path", {"fill": rrect(1.8, 1.8, 14.2, 14.2, 2.4) + " " + x_shape(8, 8, 3.6, 1.7), "evenodd": True},
                          "filled square with a cross", "Error"),
    "exclamationmark.triangle.fill": ("path", {"fill": "M8,1.4 L15,14 L1,14 Z " + rrect(7.15, 5.4, 8.85, 10.2, 0.8) + " " + circle(8, 12.1, 0.95),
                                               "evenodd": True}, "filled triangle with !", "Warning"),
    "info.circle": ("path", {"stroke": circle(8, 8, 6.2) + " M8,7.2 V11.4", "fill": circle(8, 4.9, 0.9)}, "circle with i", "Notice"),
    "checkmark.circle.fill": ("path", {"fill": circle(8, 8, 6.4) + " " + thick([(4.7, 8.2), (7, 10.5), (11.4, 5.7)], 1.7), "evenodd": True},
                              "filled circle with a check", "Done"),

    # Assistant & AI (AIPanel.swift)
    "photo.badge.plus": ("path", {"stroke": rrect(1.4, 2.6, 12.2, 11.4, 1.6) + " M1.8,10 L5,6.8 L8,9.6 L9.6,8.2 L11.8,10.2 M12.2,10 V15 M9.7,12.5 H14.7"},
                         "picture with a plus", "Generate"),
    "arrow.up.left.and.arrow.down.right": ("glyph", "E740", "FullScreen", "Expand"),
    "eyedropper.halffull": ("glyph", "EF3C", "Eyedropper", "Match Color; Auto Color"),
    "lightbulb.max": ("glyph", "EA80", "Lightbulb", "Relight"),
    "eraser.line.dashed": ("glyph", "E75C", "EraseTool", "Clean Up"),
    "sun.max.trianglebadge.exclamationmark": ("glyph", "E706", "Brightness", "Remove Reflections"),
    "wand.and.rays": ("path", {"stroke": "M2.4,13.6 L9,7 M11.4,1.6 V3.2 M14.4,4.6 H12.8 M13.6,2.4 L12.6,3.4 M13.6,7.6 L12.6,6.6 M9.2,2.4 L10.2,3.4"},
                      "wand with rays", "Harmonize; Auto Enhance"),
    "arrow.up.backward.and.arrow.down.forward": ("glyph", "E741", "ResizeTouchLarger", "Upscale"),
    "camera.filters": ("path", {"stroke": circle(8, 5.4, 3.5) + " " + circle(5.4, 10, 3.5) + " " + circle(10.6, 10, 3.5)},
                       "three circles", "Presets; preset rows; Photo Filter"),
    "camera.aperture": ("path", {"stroke": aperture(8, 8, 6.3)}, "aperture", "Lens Blur; filters in the palette"),
    "cpu": ("glyph", "EEA1", "CPU", "On This Mac section"),
    "person.crop.rectangle": ("path", {"stroke": rrect(1.6, 1.6, 14.4, 14.4, 2) + " " + PERSON}, "person in a frame", "Remove Background"),
    "person.and.background.dotted": ("path", {"stroke": dashed_rect(1.6, 1.6, 14.4, 14.4) + " " + PERSON}, "person, dotted background",
                                     "Select Subject"),
    "person.2": ("glyph", "E716", "People", "Select People"),
    "cloud.sun": ("glyph", "E753", "Cloud", "Select Sky"),
    "doc.viewfinder": ("glyph", "E8FE", "Scan", "Straighten Document"),
    "text.viewfinder": ("glyph", "E8FE", "Scan", "Image Text to Layers"),
    "key": ("glyph", "E8D7", "Permissions", "Add your OpenRouter key"),
    "bookmark": ("glyph", "E8A4", "Bookmarks", "Saved actions"),
    "stop.circle": ("path", {"stroke": circle(8, 8, 6.2), "fill": rrect(5.6, 5.6, 10.4, 10.4, 0.8)}, "circle with a square", "Cancel job"),
    "circle": ("glyph", "EA3A", "CircleRing", "Plan step waiting"),
    "xmark.circle": ("path", {"stroke": circle(8, 8, 6.2) + " M5.6,5.6 L10.4,10.4 M10.4,5.6 L5.6,10.4"}, "circle with a cross", "Canceled job"),
    "xmark.circle.fill": ("path", {"fill": circle(8, 8, 6.4) + " " + x_shape(8, 8, 3.2, 1.5), "evenodd": True},
                          "filled circle with a cross", "Clear reference"),
    "hammer": ("glyph", "E90F", "Repair", "Palette rows for tools"),
    "slider.horizontal.3": ("path", {"stroke": "M2,4 H8.4 M11.6,4 H14 M2,8 H3.4 M6.6,8 H14 M2,12 H9.4 M12.6,12 H14 " +
                                               circle(10, 4, 1.6) + " " + circle(5, 8, 1.6) + " " + circle(11, 12, 1.6)},
                            "three sliders", "Adjustment rows; Channel Mixer; smart filter settings"),
    "star.leadinghalf.filled": ("path", {"stroke": poly(star5(8, 8.4, 6.6, 2.8)), "fill": poly(clip_left(star5(8, 8.4, 6.6, 2.8), 8))},
                                "star, left half filled", "Batch rating"),
    "photo.stack": ("glyph", "E7AA", "PhotoCollection", "Batch Process"),
    "sparkle.magnifyingglass": ("glyph", "E721", "Search", "Welcome: Just ask"),

    # Settings (SettingsView.swift)
    "gearshape": ("glyph", "E713", "Settings", "General"),
    "gauge.with.dots.needle.67percent": ("glyph", "EC4A", "SpeedHigh", "Performance"),
    "applepencil.tip": ("glyph", "E76D", "InkingTool", "Tablet"),
    "arrow.up.arrow.down": ("path", {"stroke": "M5,13.6 V2.6 M2.5,5.1 L5,2.6 L7.5,5.1 M11,2.6 V13.6 M8.5,11.1 L11,13.6 L13.5,11.1"},
                            "up and down arrows", "Sort"),

    # Adjustments (Adjustments.swift, AdjustmentKind.symbolName)
    "sun.max": ("glyph", "E706", "Brightness", "Brightness/Contrast"),
    "chart.bar": ("path", {"stroke": rrect(2.4, 8.4, 5.2, 13.4, 0.6) + " " + rrect(6.6, 3, 9.4, 13.4, 0.6) + " " + rrect(10.8, 5.8, 13.6, 13.4, 0.6)},
                  "bar chart", "Levels"),
    "plusminus.circle": ("path", {"stroke": circle(8, 8, 6.2) + " M8,4.6 V8.8 M5.9,6.7 H10.1 M5.9,11 H10.1"}, "circle with ±", "Exposure"),
    "paintpalette": ("glyph", "E790", "Color", "Hue/Saturation"),
    "drop.triangle": ("path", {"stroke": DROP + " " + poly([(8, 7.6), (10.3, 11.6), (5.7, 11.6)])}, "drop with a triangle", "Vibrance"),
    "scalemass": ("path", {"stroke": poly([(3.2, 13.6), (4.9, 6.2), (11.1, 6.2), (12.8, 13.6)]) + " " + circle(8, 4.1, 1.7)},
                  "weight", "Color Balance"),
    "circle.righthalf.filled.inverse": ("path", {"stroke": circle(8, 8, 6), "fill": "M8,2 A6,6 0 0 0 8,14 Z"},
                                        "circle, left half filled", "Invert"),
    "square.stack.3d.down.right": ("path", {"stroke": rrect(1.8, 1.8, 8.6, 8.6, 1) + " M5.2,8.6 V11.6 A0.8,0.8 0 0 0 6,12.4 H11.4 "
                                                       "M8.6,5.2 H11.4 A0.8,0.8 0 0 1 12.2,6 V12.4 M9,12.4 V14.2 H14.2 V9 H12.2"},
                                   "stacked squares", "Posterize"),
    "circle.and.line.horizontal": ("path", {"stroke": circle(8, 6.6, 4.2) + " M1.8,13.6 H14.2"}, "circle over a line", "Threshold"),
    "rectangle.righthalf.inset.filled": ("path", {"stroke": rrect(1.4, 3.2, 14.6, 12.8, 1.6), "fill": rrect(8.6, 5.2, 12.6, 10.8, 0.6)},
                                         "rectangle, right half filled", "Gradient Map"),

    # The window's own buttons (Windows only).
    "window.minimize": ("glyph", "E921", "ChromeMinimize", "Minimize (caption button)"),
    "window.maximize": ("glyph", "E922", "ChromeMaximize", "Maximize (caption button)"),
    "window.restore": ("glyph", "E923", "ChromeRestore", "Restore Down (caption button)"),
    "window.close": ("glyph", "E8BB", "ChromeClose", "Close (caption button)"),
}


def cs_string(s):
    return "null" if s is None else '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def main(root: str) -> None:
    os.makedirs(os.path.join(root, "Icons"), exist_ok=True)
    os.makedirs(os.path.join(root, "tools", "out"), exist_ok=True)

    lines = ["// <auto-generated> by tools/icons.py from its ICONS table; edit the table, not this file. </auto-generated>",
             "namespace Glimmer.App.Icons;", "", "public static partial class IconCatalog", "{",
             "    /// <summary>Every SF Symbol name the original's views use, and what draws it on Windows.</summary>",
             "    public static readonly IReadOnlyDictionary<string, IconSpec> BySfSymbol = new Dictionary<string, IconSpec>(StringComparer.Ordinal)",
             "    {"]
    rows = []
    for name, spec in ICONS.items():
        kind, value, windows, where = spec[:4]
        extra = spec[4] if len(spec) > 4 else {}
        if kind == "glyph":
            lines.append(f'        [{cs_string(name)}] = IconSpec.FromGlyph(\'\\u{value}\', mirror: {str(extra.get("mirror", False)).lower()}),')
            rows.append((name, f"glyph U+{value} {windows}", where))
        elif kind == "text":
            lines.append(f'        [{cs_string(name)}] = IconSpec.FromText({cs_string(value)}, italic: {str(extra.get("italic", False)).lower()}),')
            rows.append((name, f'text "{value}"', where))
        else:
            lines.append(f'        [{cs_string(name)}] = IconSpec.FromPath({cs_string(value.get("stroke"))}, {cs_string(value.get("fill"))}, '
                         f'evenOdd: {str(value.get("evenodd", False)).lower()}),')
            rows.append((name, f"path ({windows})", where))
    lines += ["    };", "}", ""]
    with open(os.path.join(root, "Icons", "IconCatalog.g.cs"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines))

    md = ["# SF Symbols → Windows icons", "",
          "Generated by `tools/icons.py` (the table lives there). Glyphs are Segoe Fluent Icons code points; Windows 10 draws",
          "the same code points from Segoe MDL2 Assets. Paths are Glimmer's own, drawn in a 16 × 16 box.", "",
          "| SF Symbol | Windows | Used for |", "|---|---|---|"]
    md += [f"| `{n}` | {w} | {u} |" for n, w, u in rows]
    with open(os.path.join(root, "Icons", "SF-Symbols-to-Windows.md"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(md) + "\n")

    cells = []
    for name, spec in ICONS.items():
        if spec[0] != "path":
            continue
        value = spec[1]
        svg = []
        for size in (16, 24, 48):
            s = size / 16
            body = ""
            rule = "evenodd" if value.get("evenodd") else "nonzero"
            if value.get("fill"):
                body += f'<path d="{value["fill"]}" fill="#e8e9ef" fill-rule="{rule}"/>'
            if value.get("stroke"):
                body += (f'<path d="{value["stroke"]}" fill="none" stroke="#e8e9ef" stroke-width="1.3" '
                         f'stroke-linecap="round" stroke-linejoin="round"/>')
            svg.append(f'<svg width="{size}" height="{size}" viewBox="0 0 16 16" style="margin:4px">{body}</svg>')
        cells.append(f'<div class="c">{"".join(svg)}<div class="n">{html.escape(name)}</div></div>')
    page = ("<html><body style='background:#16171c;color:#969aa7;font:11px sans-serif;margin:8px'>"
            "<style>.c{display:inline-block;width:150px;height:96px;vertical-align:top;margin:2px;"
            "border:1px solid #2c2e37;border-radius:6px;padding:4px}.n{word-break:break-all}</style>"
            + "".join(cells) + "</body></html>")
    with open(os.path.join(root, "tools", "out", "icons-preview.html"), "w", encoding="utf-8") as fh:
        fh.write(page)
    print(f"{len(ICONS)} icons ({sum(1 for s in ICONS.values() if s[0] == 'glyph')} glyphs, "
          f"{sum(1 for s in ICONS.values() if s[0] == 'path')} paths, {sum(1 for s in ICONS.values() if s[0] == 'text')} texts)")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
