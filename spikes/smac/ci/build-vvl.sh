#!/usr/bin/env bash
# Spike S1: build the Khronos validation layer from a pinned tag (no macOS binaries are published on
# GitHub; the LunarG SDK is deliberately not used) and lay it out in $VVL_DIR with a relative manifest.
# Env: WORK, RESULTS, VVL_DIR
set -euo pipefail
VVL_TAG=vulkan-sdk-1.4.363.1
VVL_COMMIT=317cff060893783f96d4174f50491e6374eb3714
log() { echo "$*" | tee -a "$RESULTS"; }

t=$SECONDS
git clone -q --depth 1 --branch "$VVL_TAG" https://github.com/KhronosGroup/Vulkan-ValidationLayers.git "$WORK/vvl"
head=$(git -C "$WORK/vvl" rev-parse HEAD)
[ "$head" = "$VVL_COMMIT" ] || { echo "VVL commit $head != $VVL_COMMIT"; exit 1; }
cmake -S "$WORK/vvl" -B "$WORK/vvl/build" -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=Release -DUPDATE_DEPS=ON \
  -DBUILD_TESTS=OFF -DBUILD_WERROR=OFF -DCMAKE_OSX_ARCHITECTURES=arm64 > "$WORK/vvl-configure.log" 2>&1 \
  || { tail -80 "$WORK/vvl-configure.log"; exit 1; }
tc=$((SECONDS - t))
cmake --build "$WORK/vvl/build" --parallel "$(sysctl -n hw.ncpu)" > "$WORK/vvl-build.log" 2>&1 \
  || { tail -80 "$WORK/vvl-build.log"; exit 1; }
log "- Vulkan-ValidationLayers $VVL_TAG ($head): clone+configure (incl. UPDATE_DEPS dependency builds) ${tc} s, total $((SECONDS - t)) s"

DYLIB=$(find "$WORK/vvl/build" -name 'libVkLayer_khronos_validation.dylib' | head -1)
JSON=$(find "$WORK/vvl/build" -name 'VkLayer_khronos_validation.json' | head -1)
echo "layer dylib: $DYLIB"; echo "layer json: $JSON"
mkdir -p "$VVL_DIR"
cp -L "$DYLIB" "$VVL_DIR/"
python3 - "$JSON" "$VVL_DIR/VkLayer_khronos_validation.json" <<'EOF'
import json, sys
j = json.load(open(sys.argv[1]))
j["layer"]["library_path"] = "./libVkLayer_khronos_validation.dylib"
json.dump(j, open(sys.argv[2], "w"), indent=2)
EOF
head -c 600 "$VVL_DIR/VkLayer_khronos_validation.json"; echo
log "- bundled \`libVkLayer_khronos_validation.dylib\`: $(du -h "$VVL_DIR/libVkLayer_khronos_validation.dylib" | cut -f1)"
