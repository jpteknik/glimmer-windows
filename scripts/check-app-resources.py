"""Checks that a built Glimmer.dll carries every resource the app loads by name at start-up.

The first build of the app exited at start-up because MSBuild had moved Strings.sv.json into the Swedish satellite
assembly (it reads ".sv." in a file name as a culture). The build succeeded and nothing noticed until the app ran.

Usage: python3 scripts/check-app-resources.py <path to Glimmer.dll>     (exit 0 = all there, 1 = something missing)
"""
import pathlib
import re
import sys

dll = pathlib.Path(sys.argv[1]).read_bytes()
source = pathlib.Path(__file__).resolve().parent.parent / "src" / "Glimmer.App"

# Every LogicalName the project embeds: each must be in the main assembly, not in a satellite.
names = re.findall(r'LogicalName="([^"]+)"', (source / "Glimmer.App.csproj").read_text(encoding="utf-8"))
missing = [n for n in names if n.encode("utf-8") not in dll]
for n in names:
    print(("MISSING " if n in missing else "ok      ") + n)
if not names:
    print("no LogicalName found in Glimmer.App.csproj: the check can't see anything")
    sys.exit(1)
sys.exit(1 if missing else 0)
