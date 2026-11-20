#!/usr/bin/env python3
"""Lists the scratch project's source files per folder with line counts, and flags any bin/ or obj/ folder in the tree."""
import os
import sys

root = sys.argv[1]
skip = {"out", "__pycache__"}
total_files = total_lines = 0
stray = []
for folder, dirs, files in sorted(os.walk(root)):
    rel = os.path.relpath(folder, root)
    for d in list(dirs):
        if d in ("bin", "obj"):
            stray.append(os.path.join(rel, d))
    dirs[:] = sorted(d for d in dirs if d not in skip and d not in ("bin", "obj"))
    rows = []
    for name in sorted(files):
        if not name.endswith((".cs", ".xaml", ".json", ".csproj", ".manifest", ".py", ".md")):
            continue
        path = os.path.join(folder, name)
        with open(path, encoding="utf-8", errors="replace") as f:
            n = sum(1 for _ in f)
        rows.append(f"{name}({n})")
        total_files += 1
        total_lines += n
    if rows:
        print(f"{rel}: " + ", ".join(rows))
print(f"TOTAL {total_files} files, {total_lines} lines")
print("bin/obj in tree: " + (", ".join(stray) if stray else "none"))
