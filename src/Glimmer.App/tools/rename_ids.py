#!/usr/bin/env python3
"""One-off: the Swift type names stay as they are in the port (PORTING.md), so CommandId -> CommandID, ToolId -> ToolID."""
import re
import sys

for path in sys.argv[1:]:
    text = open(path, encoding="utf-8").read()
    new = re.sub(r"\bCommandId\b", "CommandID", text)
    new = re.sub(r"\bToolId\b", "ToolID", new)
    if new != text:
        open(path, "w", encoding="utf-8", newline="\n").write(new)
        print("updated", path)
