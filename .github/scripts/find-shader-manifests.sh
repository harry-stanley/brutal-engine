#!/usr/bin/env bash
# Prints every committed shader manifest (a file named exactly shaders.cook.json) under src/, tests/, samples/ and
# tools/ of a git checkout, one repository-relative path per line, in git's index order (sorted by path bytes).
# The cook job in ci.yml runs `brutal-cook shader --check` over them (docs/design/cook.md, "Manifests").
#
# Usage: find-shader-manifests.sh [repository root]   (default: the current directory)
#
# Only tracked files count, so build output (bin/, obj/ copies of a test fixture) and untracked scratch files never
# do. Paths are printed raw, without git's quoting of unusual characters. Prints nothing when there is no manifest;
# fails if the root is empty, isn't a git checkout, or a path contains a newline.
set -euo pipefail

if [ $# -gt 1 ]; then
  echo "usage: find-shader-manifests.sh [repository root]" >&2
  exit 2
fi
root=${1-.}
if [ -z "$root" ]; then
  echo "find-shader-manifests.sh: the repository root is empty." >&2
  exit 2
fi

# :(glob) makes ** match whole directories only, so the last segment must be exactly shaders.cook.json. -z turns
# off git's quoting; a path with a newline can't be printed one per line, so it fails instead.
git -C "$root" ls-files -z -- \
  ':(glob)src/**/shaders.cook.json' ':(glob)tests/**/shaders.cook.json' \
  ':(glob)samples/**/shaders.cook.json' ':(glob)tools/**/shaders.cook.json' |
  while IFS= read -r -d '' manifest; do
    if [[ "$manifest" == *$'\n'* ]]; then
      echo "find-shader-manifests.sh: a manifest path contains a newline: $(printf '%q' "$manifest")" >&2
      exit 1
    fi
    printf '%s\n' "$manifest"
  done
