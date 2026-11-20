#!/usr/bin/env python3
"""Checks the Mac → Windows wording rules in Localization/WindowsStrings.sv.json (applied by Loc.Platform):

  1. idempotent: applying the rules to their own output changes nothing (a string already in Windows words stays);
  2. Windows words pass through untouched (strings the AI port has already rewritten in code);
  3. the rules still do their job on the Mac phrases.

    python3 tools/platform_rules_test.py <project dir> [--selftest]

--selftest adds a rule that is not idempotent ("PC" -> "this PC") and requires check 1 to fail.
"""
import json
import os
import re
import sys

root = sys.argv[1]
selftest = "--selftest" in sys.argv
table = json.load(open(os.path.join(root, "Localization", "Strings.sv.json"), encoding="utf-8"))
rules = json.load(open(os.path.join(root, "Localization", "WindowsStrings.sv.json"), encoding="utf-8"))["platform"]
if selftest:
    rules["en"] = rules["en"] + [[r"\bPC\b", "this PC"]]


def apply(lang, s):
    for pattern, replacement in rules[lang]:
        s = re.sub(pattern, replacement, s)
    return s


problems = []
samples = {"en": list(table["strings"].keys()), "sv": [v for v in table["strings"].values() if v]}
for lang, strings in samples.items():
    for s in strings:
        once = apply(lang, s)
        if apply(lang, once) != once:
            problems.append(f"not idempotent ({lang}): {s!r} -> {once!r} -> {apply(lang, once)!r}")

windows_words = {
    "en": ["Run it on this PC", "On This PC", "Show in File Explorer", "Saved with your Windows account", "Press Ctrl+K",
           "Alt+click to exclude", "A photo editor for Windows", "your PC"],
    "sv": ["Kör på den här datorn", "På den här datorn", "Visa i Utforskaren", "Sparad med ditt Windows-konto", "Tryck Ctrl+K",
           "Alt+klicka för att utesluta", "din dator"],
}
for lang, strings in windows_words.items():
    for s in strings:
        if apply(lang, s) != s:
            problems.append(f"changes Windows words ({lang}): {s!r} -> {apply(lang, s)!r}")

expected = {
    "en": [("Heal (on this Mac, free)", "Heal (on this PC, free)"), ("Show in Finder", "Show in File Explorer")],
    "sv": [("Läk (på den här Macen, gratis)", "Läk (på den här datorn, gratis)"), ("Visa i Finder", "Visa i Utforskaren")],
}
for lang, pairs in expected.items():
    for mac, win in pairs:
        if apply(lang, mac) != win:
            problems.append(f"rule missing ({lang}): {mac!r} -> {apply(lang, mac)!r}, expected {win!r}")

for p in problems:
    print(p)
checked = sum(len(v) for v in samples.values())
print(f"{len(problems)} problems ({checked} catalogue strings, {sum(len(v) for v in windows_words.values())} Windows phrases)")
if selftest:
    ok = any(p.startswith("not idempotent") for p in problems)
    print("selftest", "PASS" if ok else "FAIL")
    sys.exit(0 if ok else 1)
sys.exit(1 if problems else 0)
