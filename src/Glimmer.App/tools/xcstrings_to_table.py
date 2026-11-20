#!/usr/bin/env python3
"""Builds the app's English -> Swedish string table from the original's String Catalog.

    python3 -I tools/xcstrings_to_table.py <glimmer-native>/App/Localizable.xcstrings Localization/Strings.sv.json

The catalogue is keyed by the English text as Swift writes it, with printf placeholders (%@, %lld, %1$@).
The C# code writes the same English with .NET composite placeholders, so both sides are converted:
"Exported %@ (%@)." becomes "Exported {0} ({1})." and its Swedish "Exporterade %@ (%@)." becomes
"Exporterade {0} ({1}).". Positional placeholders (%2$@) keep their position. %% becomes %, and a literal
brace in a text that has placeholders is doubled, as string.Format wants.

Output (UTF-8 JSON, sorted, one entry per line so diffs stay readable):
  strings  english -> swedish (null when the catalogue has no Swedish: the English is shown)
  keyed    key -> {"en": ..., "sv": ...} for entries whose key is not their English text (layer.name.group)
  plurals  english "other" form -> {"en": {"one", "other"}, "sv": {"one", "other"}}
Prints counts, and every key that two catalogue entries map to (the first one wins).
"""
import json
import re
import sys

# Only what a String Catalog makes of a Swift interpolation: %@ (text), %lld / %d (integers), %f / %lf with an
# optional precision (numbers), optionally positional (%2$@), and %% for a percent sign. A wider printf pattern
# took the literal "100 % and" in "WebP is lossless at 100 % and lossy below." for the placeholder "% a".
PLACEHOLDER = re.compile(r"%(?:(\d+)\$)?(?:\.\d+)?(?:ll|l)?([@duf])|%%")


def to_dotnet(template: str) -> tuple[str, int]:
    """Converts a printf template to .NET composite format; returns the text and the placeholder count."""
    has_placeholders = PLACEHOLDER.search(template.replace("%%", "")) is not None
    out, sequential, count, pos = [], 0, 0, 0
    for m in PLACEHOLDER.finditer(template):
        literal = template[pos:m.start()]
        if has_placeholders:
            literal = literal.replace("{", "{{").replace("}", "}}")
        out.append(literal)
        if m.group(0) == "%%":
            out.append("%")
        else:
            if m.group(1):
                index = int(m.group(1)) - 1
            else:
                index = sequential
                sequential += 1
            out.append("{%d}" % index)
            count += 1
        pos = m.end()
    tail = template[pos:]
    if has_placeholders:
        tail = tail.replace("{", "{{").replace("}", "}}")
    out.append(tail)
    return "".join(out), count


def unit(body):
    return (body or {}).get("stringUnit", {}).get("value")


def main(src: str, dst: str) -> None:
    catalogue = json.load(open(src, encoding="utf-8"))
    strings, keyed, plurals = {}, {}, {}
    conflicts, untranslated, with_placeholders = [], 0, 0

    for key, entry in catalogue["strings"].items():
        if key == "":
            continue
        locs = entry.get("localizations", {})
        en, sv = locs.get("en", {}), locs.get("sv", {})
        if "variations" in en or "variations" in sv:
            forms = {}
            for lang, body in (("en", en), ("sv", sv)):
                plural = body.get("variations", {}).get("plural", {})
                forms[lang] = {f: to_dotnet(unit(v) or "")[0] for f, v in plural.items()}
            other = forms["en"].get("other") or to_dotnet(key)[0]
            plurals[other] = forms
            continue
        english_template = unit(en) or key
        english, n = to_dotnet(english_template)
        swedish_raw = unit(sv)
        swedish = to_dotnet(swedish_raw)[0] if swedish_raw is not None else None
        if swedish is None:
            untranslated += 1
        if n:
            with_placeholders += 1
        if unit(en) is not None and to_dotnet(key)[0] != english:
            # A key that is not its own English text (an explicit key with a default value). Placeholder keys whose
            # English is only the positional form of the key are not that: they are listed under strings.
            if PLACEHOLDER.search(key) is None:
                keyed[key] = {"en": english, "sv": swedish}
                continue
        if english in strings and strings[english] != swedish:
            conflicts.append((key, english))
            continue
        strings[english] = swedish

    table = {"source": "glimmer-native App/Localizable.xcstrings", "strings": dict(sorted(strings.items())),
             "keyed": dict(sorted(keyed.items())), "plurals": dict(sorted(plurals.items()))}
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        json.dump(table, f, ensure_ascii=False, indent=0, sort_keys=False)
        f.write("\n")

    translated = sum(1 for v in strings.values() if v is not None)
    print(f"strings: {len(strings)} ({translated} with Swedish, {len(strings) - translated} English only), "
          f"{with_placeholders} with placeholders; keyed: {len(keyed)}; plurals: {len(plurals)}")
    for key, english in conflicts:
        print(f"conflict: {key!r} -> {english!r} (kept the first entry)")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
