#!/usr/bin/env python3
"""One-off edits to Views/Sheets/AppSheets.cs: Loc.T already applies the platform wording, and StatusLine takes
Verbatim, not Text. Prints the number of each replacement."""
import re
import sys

path = sys.argv[1]
text = open(path, encoding="utf-8").read()
text, a = re.subn(r'Loc\.Platform\(Loc\.T\(("(?:[^"\\]|\\.)*")\)\)', r'Loc.T(\1)', text)
text, b = re.subn(r'\b(failure|problem)\.Text = ', r'\1.Verbatim = ', text)
text, c = re.subn(r'new StatusLine \{ Kind = StatusKind\.Error, Text = failure \}', 'new StatusLine { Kind = StatusKind.Error, Verbatim = failure }', text)
text, d = re.subn(r'Loc\.Platform\(message\)', 'message', text)
open(path, "w", encoding="utf-8", newline="\n").write(text)
print(a, b, c, d)
