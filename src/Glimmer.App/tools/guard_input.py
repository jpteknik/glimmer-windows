#!/usr/bin/env python3
"""Adds `if (FromEditor(e)) return;` after `base.OnX(e);` in the canvas's input overrides, and makes the wheel handler
callable from the text editor. Prints what it changed; exits 1 if an expected spot is missing."""
import re
import sys

path = sys.argv[1]
text = open(path, encoding="utf-8").read()
names = ["OnMouseDown", "OnMouseMove", "OnMouseUp", "OnStylusDown", "OnStylusMove", "OnStylusUp", "OnStylusInAirMove",
         "OnTouchDown", "OnTouchMove", "OnTouchUp", "OnKeyDown", "OnKeyUp", "OnDragOver", "OnDrop"]
missing = []
for name in names:
    pattern = "        base.%s(e);\n" % name
    if text.count(pattern) != 1:
        missing.append(name)
        continue
    text = text.replace(pattern, pattern + "        if (FromEditor(e)) return;\n")

old_wheel = """    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Controller is not { } canvas) return;"""
new_wheel = """    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        HandleWheel(e);
    }

    private void HandleWheel(MouseWheelEventArgs e)
    {
        if (Controller is not { } canvas) return;"""
if text.count(old_wheel) != 1:
    missing.append("OnMouseWheel")
else:
    text = text.replace(old_wheel, new_wheel)

old_hook = """        const int WM_MOUSEHWHEEL = 0x020E;
"""
new_hook = """        const int WM_MOUSEHWHEEL = 0x020E, WM_KEYDOWN = 0x0100, VK_RETURN = 0x0D;
        // Enter on the numeric keypad ends text editing, Return doesn't; only the message's extended-key bit tells them apart.
        if (msg == WM_KEYDOWN && wParam.ToInt64() == VK_RETURN) keypadEnter = ((lParam.ToInt64() >> 24) & 1) != 0;
"""
if text.count(old_hook) != 1:
    missing.append("WindowHook")
else:
    text = text.replace(old_hook, new_hook)

open(path, "w", encoding="utf-8", newline="\n").write(text)
print("changed: %d guards, wheel, hook; missing: %s" % (len(names) - len([m for m in missing if m in names]), missing or "none"))
sys.exit(1 if missing else 0)
