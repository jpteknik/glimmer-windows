#!/usr/bin/env python3
"""Renames calls of a method in one file: rename_call.py <file> <old name> <new name> (word-boundary, followed by "(")."""
import re
import sys

path, old, new = sys.argv[1:4]
text = open(path, encoding="utf-8").read()
updated = re.sub(r"\b" + re.escape(old) + r"\(", new + "(", text)
open(path, "w", encoding="utf-8", newline="\n").write(updated)
print(f"{text.count(old + '(')} occurrence(s) renamed in {path}")
