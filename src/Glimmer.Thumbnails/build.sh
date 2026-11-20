#!/usr/bin/env bash
# Builds Glimmer's File Explorer thumbnail handler (GlimmerThumbnails.dll) for x64 and ARM64 Windows, and the ZIP reader
# it contains as a Linux library for the tests, with llvm-mingw (https://github.com/mstorsjo/llvm-mingw; any recent
# release, here 20261117, ucrt, ubuntu-22.04-x86_64). The results go where the app and the tests take them from:
#
#   src/Glimmer.App/Shell/GlimmerThumbnails-x64.dll, -arm64.dll   embedded in Glimmer.exe, registered for the user
#   tests/Glimmer.Core.Tests/Native/libglimmer_zip.so              the same glimmer_zip.c, for the tests on Linux
#
# The app's own build needs none of this: the DLLs are kept in the repository, so a .NET SDK is enough to build Glimmer.
# Run this script after changing anything in src/Glimmer.Thumbnails.
#
#   LLVM_MINGW=/path/to/llvm-mingw src/Glimmer.Thumbnails/build.sh
set -euo pipefail
here="$(dirname "$(readlink -f "$0")")"
root="$(dirname "$(dirname "$here")")"
toolchain="${LLVM_MINGW:?set LLVM_MINGW to an llvm-mingw folder}"
shell="$root/src/Glimmer.App/Shell"
native="$root/tests/Glimmer.Core.Tests/Native"
mkdir -p "$shell" "$native"

objects="$(mktemp -d "${TMPDIR:-/tmp}/glimmer-thumbnails.XXXXXX")"
trap 'rm -rf "$objects"' EXIT
for arch in x86_64 aarch64; do
  name=$([ "$arch" = x86_64 ] && echo x64 || echo arm64)
  "$toolchain/bin/$arch-w64-mingw32-clang" -std=c11 -O2 -Wall -Wextra -Werror -c -o "$objects/glimmer_zip-$name.o" "$here/glimmer_zip.c"
  "$toolchain/bin/$arch-w64-mingw32-clang++" -std=c++17 -O2 -Wall -Wextra -Werror -fno-exceptions -fno-rtti \
    -shared -static -s -o "$shell/GlimmerThumbnails-$name.dll" \
    "$here/ThumbnailProvider.cpp" "$objects/glimmer_zip-$name.o" "$here/GlimmerThumbnails.def" \
    -lole32 -lshlwapi -lgdi32 -luuid -Wl,--no-insert-timestamp
  echo "Built $shell/GlimmerThumbnails-$name.dll"
done

# The ZIP reader on its own, freestanding (it calls nothing), as a Linux library the tests load. -z defs fails the link
# if the compiler made it call anything after all.
"$toolchain/bin/clang" --target=x86_64-unknown-linux-gnu -ffreestanding -nostdlib -fno-builtin -fPIC -shared -O2 \
  -Wall -Wextra -Werror -fuse-ld=lld -Wl,-z,defs -Wl,--build-id=none -o "$native/libglimmer_zip.so" "$here/glimmer_zip.c"
echo "Built $native/libglimmer_zip.so"
