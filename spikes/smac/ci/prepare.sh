#!/usr/bin/env bash
# Spike S1: fetch pinned MoltenVK, build the pinned Vulkan loader and lay out an app bundle dir:
#   $APP/VkSmoke.dll + libvulkan.1.dylib + libMoltenVK.dylib + MoltenVK_icd.json
# Env: WORK (scratch dir), APP (output dir), RESULTS (markdown file to append to)
set -euo pipefail

MVK_TAG=v1.4.2
MVK_ASSET=MoltenVK-macos.tar
MVK_SHA256=f95765a6229cb7b915990a2890ce12ebe36a730b021545d3d52ae69ce4c4024e # GitHub release asset digest
LOADER_TAG=vulkan-sdk-1.4.363.0
LOADER_COMMIT=fa218055ff4948b159f31d43f800c808a13d51df

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
mkdir -p "$WORK" "$APP"
log() { echo "$*" | tee -a "$RESULTS"; }

# --- MoltenVK (official release artifact) ---
t=$SECONDS
mkdir -p "$WORK/mvk"
curl -fsSL --retry 3 -o "$WORK/mvk/$MVK_ASSET" \
  "https://github.com/KhronosGroup/MoltenVK/releases/download/$MVK_TAG/$MVK_ASSET"
got=$(shasum -a 256 "$WORK/mvk/$MVK_ASSET" | cut -d' ' -f1)
log "- MoltenVK $MVK_TAG $MVK_ASSET sha256 \`$got\` (expected \`$MVK_SHA256\`), $(du -h "$WORK/mvk/$MVK_ASSET" | cut -f1)"
[ "$got" = "$MVK_SHA256" ] || { echo "MoltenVK sha256 mismatch"; exit 1; }
tar -xf "$WORK/mvk/$MVK_ASSET" -C "$WORK/mvk"
MVK_DYLIB=$(find "$WORK/mvk" -path '*dynamic*' -name libMoltenVK.dylib | grep -i macos | head -1)
MVK_JSON=$(find "$(dirname "$MVK_DYLIB")" -name 'MoltenVK_icd.json' | head -1)
echo "MoltenVK dylib: $MVK_DYLIB"; echo "MoltenVK ICD json: $MVK_JSON"; cat "$MVK_JSON"
log "- MoltenVK download+extract: $((SECONDS - t)) s"

# --- Vulkan loader (built from pinned tag) ---
t=$SECONDS
git clone -q --depth 1 --branch "$LOADER_TAG" https://github.com/KhronosGroup/Vulkan-Loader.git "$WORK/loader"
head=$(git -C "$WORK/loader" rev-parse HEAD)
[ "$head" = "$LOADER_COMMIT" ] || { echo "loader commit $head != $LOADER_COMMIT"; exit 1; }
cmake -S "$WORK/loader" -B "$WORK/loader/build" -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=Release \
  -DUPDATE_DEPS=ON -DBUILD_TESTS=OFF -DCMAKE_OSX_ARCHITECTURES=arm64 > "$WORK/loader-configure.log" 2>&1 \
  || { tail -50 "$WORK/loader-configure.log"; exit 1; }
cmake --build "$WORK/loader/build" --parallel "$(sysctl -n hw.ncpu)" > "$WORK/loader-build.log" 2>&1 \
  || { tail -50 "$WORK/loader-build.log"; exit 1; }
LOADER_DYLIB=$(find "$WORK/loader/build" -name 'libvulkan.1.dylib' | head -1)
log "- Vulkan-Loader $LOADER_TAG ($head) clone+configure+build: $((SECONDS - t)) s (UPDATE_DEPS fetches Vulkan-Headers at the tag's known_good.json pin)"

# --- Test app ---
t=$SECONDS
dotnet build "$REPO_ROOT/spikes/smac/VkSmoke.cs" -c Release -o "$APP" -nologo -v q
log "- dotnet build VkSmoke.cs (incl. NuGet restore): $((SECONDS - t)) s"

# Bundle natives beside the app. cp -L resolves the libvulkan.1.dylib -> libvulkan.1.x.y.dylib symlink.
cp -L "$LOADER_DYLIB" "$APP/libvulkan.1.dylib"
cp -L "$MVK_DYLIB" "$APP/libMoltenVK.dylib"
# library_path "./libMoltenVK.dylib" is resolved relative to the JSON file's directory by the loader.
python3 - "$MVK_JSON" "$APP/MoltenVK_icd.json" <<'EOF'
import json, sys
j = json.load(open(sys.argv[1]))
j["ICD"]["library_path"] = "./libMoltenVK.dylib"
json.dump(j, open(sys.argv[2], "w"), indent=2)
EOF
cat "$APP/MoltenVK_icd.json"

for f in libvulkan.1.dylib libMoltenVK.dylib runtimes/osx-arm64/native/libVulkanEx.dylib; do
  echo "== $f"; file "$APP/$f"; otool -L "$APP/$f" | head -20; otool -D "$APP/$f"
  codesign -dv "$APP/$f" 2>&1 | head -5 || true
  log "- bundled \`$f\`: $(du -h "$APP/$f" | cut -f1), sha256 \`$(shasum -a 256 "$APP/$f" | cut -d' ' -f1)\`"
done
echo "== libVulkanEx exports (vk*)"; nm -gU "$APP/runtimes/osx-arm64/native/libVulkanEx.dylib" | grep -i ' _vk' | head -20 || echo "(none)"
