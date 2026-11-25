#!/usr/bin/env bash
# Builds the pinned Vulkan loader and Khronos validation layers for macOS arm64 into <out>:
#   <out>/lib/libvulkan.1.dylib
#   <out>/layers/libVkLayer_khronos_validation.dylib + VkLayer_khronos_validation.json (relative library_path)
# Usage: build-from-source.sh <pins.env> <work dir> <out dir>
set -euo pipefail

pins=$1
work=$2
out=$3
# shellcheck source=/dev/null
source "$pins"
jobs=$(sysctl -n hw.ncpu)
mkdir -p "$work" "$out/lib" "$out/layers"

# Clones <repo> at <tag>, asserts the commit, and runs a Release cmake build. UPDATE_DEPS fetches the
# dependencies (Vulkan-Headers, SPIRV-Tools, ...) at the versions pinned by the tag's known_good.json.
build() {
  local repo=$1 tag=$2 commit=$3 dir=$work/$1
  shift 3
  local start=$SECONDS
  echo "::group::Build $repo $tag"
  git clone -q --depth 1 --branch "$tag" "https://github.com/KhronosGroup/$repo.git" "$dir"
  local head
  head=$(git -C "$dir" rev-parse HEAD)
  if [ "$head" != "$commit" ]; then
    echo "::error::$repo $tag is at $head, expected $commit."
    exit 1
  fi
  cmake -S "$dir" -B "$dir/build" -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DUPDATE_DEPS=ON -DBUILD_TESTS=OFF "$@" > "$work/$repo-configure.log" 2>&1 \
    || { tail -n 80 "$work/$repo-configure.log"; exit 1; }
  cmake --build "$dir/build" --parallel "$jobs" > "$work/$repo-build.log" 2>&1 \
    || { tail -n 80 "$work/$repo-build.log"; exit 1; }
  echo "::endgroup::"
  echo "$repo $tag ($head) built in $((SECONDS - start)) s"
}

build Vulkan-Loader "$LOADER_TAG" "$LOADER_COMMIT"
# cp -L: libvulkan.1.dylib is a symlink to the versioned dylib.
cp -L "$(find "$work/Vulkan-Loader/build" -name libvulkan.1.dylib | head -n 1)" "$out/lib/libvulkan.1.dylib"

build Vulkan-ValidationLayers "$VALIDATION_LAYERS_TAG" "$VALIDATION_LAYERS_COMMIT" -DBUILD_WERROR=OFF
layers=$work/Vulkan-ValidationLayers/build
cp -L "$(find "$layers" -name libVkLayer_khronos_validation.dylib | head -n 1)" "$out/layers/"
# The loader resolves a relative library_path against the manifest's directory.
python3 -I - "$(find "$layers" -name VkLayer_khronos_validation.json | head -n 1)" \
  "$out/layers/VkLayer_khronos_validation.json" <<'EOF'
import json, sys
manifest = json.load(open(sys.argv[1]))
manifest["layer"]["library_path"] = "./libVkLayer_khronos_validation.dylib"
json.dump(manifest, open(sys.argv[2], "w"), indent=2)
EOF

ls -l "$out/lib" "$out/layers"
