#!/usr/bin/env python3
"""Checks what the XAML compiler cannot, since nothing here can run WPF:

  1. resources: every {StaticResource X} / {DynamicResource X} / FindResource("X") names a key that some dictionary
     defines (ours, or the Fluent theme's); a missing StaticResource is an exception at run time, not a build error.
  2. strings:   every English key shown through Loc is in the table built from the original's String Catalog (or in
     WindowsStrings.sv.json), i.e. word for word as in the Swift source.
  3. icons:     every SF Symbol name a view asks for has an entry in the icon catalogue.

    python3 tools/check_ui.py <project dir> <Fluent.xaml>

Exit status 1 when anything is missing. A negative control is built in: --selftest plants one fault of each kind
and requires all three to be reported.
"""
import json
import os
import re
import sys

XAML_KEY = re.compile(r'x:Key="([^"]+)"')
XAML_REF = re.compile(r'\{(?:StaticResource|DynamicResource)\s+([^}\s]+)\s*\}')
CODE_REF = re.compile(r'(?:FindResource|TryFindResource)\(\s*"([^"]+)"\s*\)')
CODE_DEF = re.compile(r'(?:Resources\[\s*"([^"]+)"\s*\]\s*=|\bSet\(\s*"([^"]+)"\s*,)')
LOC_QUOTED = re.compile(r"\{l:Loc\s+'((?:[^'\\]|\\.)*)'\s*\}")
LOC_PLAIN = re.compile(r"\{l:Loc\s+([^'}\s][^}]*)\}")
XAML_KEY_ATTRS = re.compile(r'\b(?:Title|TextKey|Label)="([^"{][^"]*)"')
CS_LOC = re.compile(r'\bLoc\.(?:T|F|Maybe|Plural)\(\s*"((?:[^"\\]|\\.)*)"')
# Helpers whose first string argument is a catalogue key.
HELPERS = ["Hint", "Slider", "Check", "Button", "Menu", "Headline", "Row", "SmallButton", "IconTextButton", "Item", "Do", "Header",
           "Action", "Secondary", "Combo", "Segmented", "Well", "Top", "Sub", "Routed", "NotStarted", "ParameterSlider", "Number",
           "Toggle", "Section", "Note", "ValueSlider", "Picker", "Field", "Choice", "Link", "Tab", "Group", "Prompt", "Strength"]
# Helpers are also called on form sections (section.Row("…")), so a dot before them is fine, except for Number:
# step.Number("scale") is a member of a model, not the view helper.
CS_HELPER = re.compile(r'(?:\b(?:' + "|".join(h for h in HELPERS if h != "Number") + r')|(?<![.\w])Number)\(\s*"((?:[^"\\]|\\.)*)"')
# Named arguments that helpers look up: footer: "…", help: "…", title: "…".
CS_NAMED = re.compile(r'\b(?:footer|help|title|header|placeholder|caption):\s*"((?:[^"\\]|\\.)*)"')
CS_KEY_PROPS = re.compile(r'\b(?:TextKey|Label|Title)\s*=\s*"((?:[^"\\]|\\.)*)"')
SYMBOL_XAML = re.compile(r'\bSymbol="([^"{][^"]*)"')
SYMBOL_CS = re.compile(r'\b(?:Symbol\s*=\s*|symbol:\s*|"symbol"\s*,\s*)"([a-z0-9.]+)"')
SYMBOL_LITERAL = re.compile(r'"([a-z]+(?:\.[a-z0-9]+)+)"')


def files(root, ext):
    for d, dirs, names in os.walk(root):
        dirs[:] = [x for x in dirs if x not in ("tools", "obj", "bin")]
        for n in names:
            if n.endswith(ext):
                yield os.path.join(d, n)


def xml_unescape(s):
    return s.replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", '"').replace("&apos;", "'")


def check(root, fluent_path):
    problems = []
    defined = set()
    for f in files(root, ".xaml"):
        defined |= set(XAML_KEY.findall(open(f, encoding="utf-8").read()))
    for f in files(root, ".cs"):
        for a, b in CODE_DEF.findall(open(f, encoding="utf-8").read()):
            defined.add(a or b)
    defined |= set(XAML_KEY.findall(open(fluent_path, encoding="utf-8").read()))
    for f in files(root, ".xaml"):
        for key in XAML_REF.findall(open(f, encoding="utf-8").read()):
            if not key.startswith("{x:") and key not in defined:
                problems.append(f"resource: {os.path.relpath(f, root)}: {key}")
    for f in files(root, ".cs"):
        for key in CODE_REF.findall(open(f, encoding="utf-8").read()):
            if key not in defined:
                problems.append(f"resource: {os.path.relpath(f, root)}: {key}")

    table = json.load(open(os.path.join(root, "Localization", "Strings.sv.json"), encoding="utf-8"))
    extra = json.load(open(os.path.join(root, "Localization", "WindowsStrings.sv.json"), encoding="utf-8"))
    known = set(table["strings"]) | set(table["keyed"]) | set(table["plurals"]) | set(extra["strings"])
    used = []
    for f in files(root, ".xaml"):
        text = open(f, encoding="utf-8").read()
        for m in LOC_QUOTED.findall(text):
            used.append((f, xml_unescape(m.replace("\\'", "'"))))
        for m in LOC_PLAIN.findall(text):
            used.append((f, xml_unescape(m.strip())))
        for m in XAML_KEY_ATTRS.findall(text):
            used.append((f, xml_unescape(m)))
    for f in files(root, ".cs"):
        if os.sep + "StandIns" + os.sep in f or os.sep + "Contract" + os.sep in f:
            continue
        text = open(f, encoding="utf-8").read()
        for rx in (CS_LOC, CS_HELPER, CS_KEY_PROPS, CS_NAMED):
            for m in rx.findall(text):
                used.append((f, m.encode().decode("unicode_escape").encode("latin-1").decode("utf-8") if "\\" in m else m))
    for f, key in used:
        if key and key not in known:
            problems.append(f"string: {os.path.relpath(f, root)}: {key!r}")

    catalogue = open(os.path.join(root, "Icons", "IconCatalog.g.cs"), encoding="utf-8").read()
    icons = set(re.findall(r'\["([^"]+)"\]\s*=', catalogue))
    wanted = []
    for f in files(root, ".xaml"):
        wanted += [(f, s) for s in SYMBOL_XAML.findall(open(f, encoding="utf-8").read())]
    for f in files(root, ".cs"):
        if f.endswith("IconCatalog.g.cs"):
            continue
        text = open(f, encoding="utf-8").read()
        wanted += [(f, s) for s in SYMBOL_CS.findall(text)]
        # SymbolName() tables and status symbols: dotted lower-case literals that look like SF names.
        if "SymbolName" in text or "Symbol(" in text or "symbol" in text:
            wanted += [(f, s) for s in SYMBOL_LITERAL.findall(text) if not s.startswith(("glimmer.", "ui.", "canvas.", "palette.", "swatches.", "settings.",
                       "onboarding.", "newdocument.", "tablet.", "layer.", "view.", "file.", "edit.", "image.", "select.", "filter.", "ai.", "tool.", "preset.",
                       "action.", "crop.", "brush."))]
    for f, s in wanted:
        if s not in icons:
            problems.append(f"icon: {os.path.relpath(f, root)}: {s}")
    return problems, len(used), len(wanted)


def main():
    # Exactly "<dir> <Fluent.xaml>" or "--selftest <dir> <Fluent.xaml>": a flag in any other place would otherwise be
    # ignored and an ordinary run reported in place of the negative control.
    args = sys.argv[1:]
    if not (len(args) == 2 and not args[0].startswith("-")) and not (len(args) == 3 and args[0] == "--selftest"):
        sys.exit(__doc__)
    if args[0] == "--selftest":
        import shutil
        import tempfile
        root, fluent = sys.argv[2], sys.argv[3]
        tmp = tempfile.mkdtemp(prefix="check-ui-selftest-")
        try:
            dst = os.path.join(tmp, "p")
            shutil.copytree(root, dst, ignore=shutil.ignore_patterns("tools"))
            with open(os.path.join(dst, "Planted.xaml"), "w", encoding="utf-8") as fh:
                fh.write('<Grid Background="{StaticResource Planted.MissingBrush}"><TextBlock Text="{l:Loc \'Planted missing string\'}"/>'
                         '<c:Icon Symbol="planted.missing.symbol"/></Grid>')
            problems, _, _ = check(dst, fluent)
            found = {p.split(":")[0] for p in problems if "Planted" in p or "planted" in p}
            ok = found == {"resource", "string", "icon"}
            print("selftest", "PASS" if ok else "FAIL", sorted(found))
            sys.exit(0 if ok else 1)
        finally:
            shutil.rmtree(tmp)
    root, fluent = sys.argv[1], sys.argv[2]
    problems, n_strings, n_icons = check(root, fluent)
    for p in sorted(set(problems)):
        print(p)
    print(f"{len(set(problems))} problems ({n_strings} string uses, {n_icons} icon uses checked)")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
