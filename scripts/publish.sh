#!/usr/bin/env bash
# Builds Glimmer for Windows from WSL/Linux into dist/ (or $GLIMMER_DIST), each file self-contained (no .NET runtime
# needed on the computer that runs it):
#   Glimmer-<version>-<rid>.exe        the app
#   Glimmer-<version>-<rid>.com        its console companion (cmd and PowerShell find a .com before an .exe, so
#                                      "Glimmer --export a.jpg b.png" waits, prints and returns the exit status; the .com
#                                      starts the .exe with the same base name)
#   Glimmer-<version>-<rid>-setup.exe  the installer: both of them for the current user, with a Start menu shortcut,
#                                      "glimmer" on PATH, the file types and an uninstaller (src/Glimmer.Setup)
# The WPF app builds here (EnableWindowsTargeting) but runs only on Windows.
#
#   scripts/publish.sh            # win-x64
#   scripts/publish.sh win-arm64
set -euo pipefail
rid="${1:-win-x64}"
root="$(dirname "$(dirname "$(readlink -f "$0")")")"
artifacts="${GLIMMER_ARTIFACTS:-/tmp/glimmer-build/publish}"
dist="${GLIMMER_DIST:-$root/dist}"
version="$(grep -o '<Version>[^<]*' "$root/Directory.Build.props" | head -1 | cut -d'>' -f2)"
single=(-c Release -r "$rid" --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true)

dotnet publish "$root/src/Glimmer.App/Glimmer.App.csproj" "${single[@]}" --artifacts-path "$artifacts"
# The companion: no other Glimmer project in it; trimming, invariant globalization and the rest are in its project file.
dotnet publish "$root/src/Glimmer.Console/Glimmer.Console.csproj" "${single[@]}" --artifacts-path "$artifacts"
# The installer, twice: without a payload it is the uninstaller that goes into the install folder; with one it installs.
dotnet publish "$root/src/Glimmer.Setup/Glimmer.Setup.csproj" "${single[@]}" --artifacts-path "$artifacts/uninstaller"
payload="$artifacts/payload-$rid"
mkdir -p "$payload"
cp "$artifacts/publish/Glimmer.App/release_$rid/Glimmer.exe" "$payload/Glimmer.exe"
cp "$artifacts/publish/Glimmer.Console/release_$rid/Glimmer.Console.exe" "$payload/Glimmer.com"
cp "$artifacts/uninstaller/publish/Glimmer.Setup/release_$rid/Glimmer.Setup.exe" "$payload/Uninstall.exe"
dotnet publish "$root/src/Glimmer.Setup/Glimmer.Setup.csproj" "${single[@]}" -p:GlimmerPayloadDir="$payload" --artifacts-path "$artifacts/installer"

mkdir -p "$dist"
cp "$payload/Glimmer.exe" "$dist/Glimmer-$version-$rid.exe"
cp "$payload/Glimmer.com" "$dist/Glimmer-$version-$rid.com"
cp "$artifacts/installer/publish/Glimmer.Setup/release_$rid/Glimmer.Setup.exe" "$dist/Glimmer-$version-$rid-setup.exe"
echo "Published $dist/Glimmer-$version-$rid.exe, .com and -setup.exe"
