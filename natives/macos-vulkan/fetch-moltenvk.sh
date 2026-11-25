#!/usr/bin/env bash
# Downloads the pinned MoltenVK release asset, verifies its SHA-256 and writes into <out>:
#   <out>/libMoltenVK.dylib + MoltenVK_icd.json (library_path relative to the manifest)
# Usage: fetch-moltenvk.sh <pins.env> <work dir> <out dir>
set -euo pipefail

pins=$1
work=$2
out=$3
# shellcheck source=/dev/null
source "$pins"
mkdir -p "$work" "$out"

archive=$work/$MOLTENVK_ASSET
curl -fsSL --retry 3 -o "$archive" \
  "https://github.com/KhronosGroup/MoltenVK/releases/download/$MOLTENVK_TAG/$MOLTENVK_ASSET"
actual=$(shasum -a 256 "$archive" | cut -d' ' -f1)
if [ "$actual" != "$MOLTENVK_SHA256" ]; then
  echo "::error::MoltenVK $MOLTENVK_TAG $MOLTENVK_ASSET SHA-256 mismatch: $actual"
  exit 1
fi
tar -xf "$archive" -C "$work"

# The macOS dynamic library, not the iOS/tvOS ones or the static XCFramework.
mvk=$(dirname "$(find "$work" -path '*/dynamic/dylib/macOS/libMoltenVK.dylib' | head -n 1)")
test -f "$mvk/libMoltenVK.dylib" && test -f "$mvk/MoltenVK_icd.json"
cp -L "$mvk/libMoltenVK.dylib" "$out/"
# The loader resolves a relative library_path against the manifest's directory.
python3 -I - "$mvk/MoltenVK_icd.json" "$out/MoltenVK_icd.json" <<'EOF'
import json, sys
manifest = json.load(open(sys.argv[1]))
manifest["ICD"]["library_path"] = "./libMoltenVK.dylib"
json.dump(manifest, open(sys.argv[2], "w"), indent=2)
EOF
echo "MoltenVK $MOLTENVK_TAG ($MOLTENVK_ASSET, SHA-256 verified) from $mvk"
