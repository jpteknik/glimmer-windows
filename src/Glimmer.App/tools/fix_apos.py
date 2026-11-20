#!/usr/bin/env python3
"""In XAML, an apostrophe inside a single-quoted markup extension argument must be written \\' (after XML has turned
&apos; back into '). Rewrites &apos; to \\' inside {l:Loc '...'} arguments of the given files."""
import re
import sys

for path in sys.argv[1:]:
    text = open(path, encoding="utf-8").read()
    new = re.sub(r"\{l:Loc '([^}]*)'\}", lambda m: "{l:Loc '" + m.group(1).replace("&apos;", "\\'") + "'}", text)
    if new != text:
        open(path, "w", encoding="utf-8", newline="\n").write(new)
        print("fixed", path)
