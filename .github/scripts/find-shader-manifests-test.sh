#!/usr/bin/env bash
# Self-test for find-shader-manifests.sh. The tier job runs it before the cook job uses the script, so a finder that
# misses a manifest fails loudly instead of silently checking fewer shaders. It works over scratch git repositories:
# files are staged, not committed, because the finder reads git's index.
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/find-shader-manifests.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
# Keep git from finding a repository above the scratch tree (the not-a-checkout case needs none).
export GIT_CEILING_DIRECTORIES=$work
failures=0
nl=$'\n'

# repo <name> [path...]: a fresh repository with each path written and staged.
repo() {
  local dir="$work/$1"
  shift
  git init -q "$dir"
  for path in "$@"; do
    mkdir -p "$dir/$(dirname "$path")"
    printf '{}\n' > "$dir/$path"
  done
  if [ $# -gt 0 ]; then
    git -C "$dir" add -- "$@"
  fi
}

# untracked <repo> <path>: a file that git doesn't track.
untracked() {
  mkdir -p "$work/$1/$(dirname "$2")"
  printf '{}\n' > "$work/$1/$2"
}

# check <description> <expected exit code> <expected output> <working directory> [arguments...]
check() {
  local description=$1 want_code=$2 want_output=$3 dir=$4 output code
  shift 4
  set +e
  output=$(cd "$dir" && bash "$script" "$@" 2> "$work/stderr")
  code=$?
  set -e
  if [ "$code" = "$want_code" ] && [ "$output" = "$want_output" ]; then
    echo "ok    $description"
  else
    echo "FAIL  $description: exit code $code (expected $want_code), output:"
    printf '%s\n' "$output" | sed 's/^/        /'
    sed 's/^/        stderr: /' "$work/stderr"
    failures=$((failures + 1))
  fi
}

# Every place a manifest may live: nested at any depth, and directly under each of the four top directories (so each
# pathspec is pinned on its own), printed in index order (byte order: 'B' < 'S' < 'a' < 's'), with spaces and
# non-ASCII names raw.
repo all \
  tools/BrutalEngine.Cook/shaders.cook.json \
  "tests/a b/ünï/shaders.cook.json" \
  src/shaders.cook.json \
  tests/shaders.cook.json \
  samples/shaders.cook.json \
  tools/shaders.cook.json \
  samples/SpriteStress/Shaders/shaders.cook.json \
  tests/BrutalEngine.Cook.Tests/Fixtures/Shaders/shaders.cook.json \
  src/BrutalEngine.Graphics.Vulkan/Shaders/shaders.cook.json
all="samples/SpriteStress/Shaders/shaders.cook.json${nl}samples/shaders.cook.json"
all+="${nl}src/BrutalEngine.Graphics.Vulkan/Shaders/shaders.cook.json${nl}src/shaders.cook.json"
all+="${nl}tests/BrutalEngine.Cook.Tests/Fixtures/Shaders/shaders.cook.json${nl}tests/a b/ünï/shaders.cook.json"
all+="${nl}tests/shaders.cook.json${nl}tools/BrutalEngine.Cook/shaders.cook.json${nl}tools/shaders.cook.json"
check "every top directory, any depth, index order, raw names" 0 "$all" "$work" all
check "no argument: the current directory" 0 "$all" "$work/all"
check "a relative root" 0 "$all" "$work/all/src" ..

# Paths that aren't manifests, or aren't under the four top directories, or aren't tracked. The near-miss names
# appear under each top directory, so each pathspec must match the last segment exactly.
near_misses=()
for top in src tests samples tools; do
  near_misses+=("$top/x/xshaders.cook.json" "$top/x/shaders.cook.json/inner.json")
done
repo others \
  tests/ok/shaders.cook.json \
  shaders.cook.json \
  docs/shaders.cook.json \
  srcx/shaders.cook.json \
  tests/x/shaders.cook.json.bak \
  tests/x/Shaders.Cook.json \
  "${near_misses[@]}"
untracked others tests/new/shaders.cook.json
untracked others tests/BrutalEngine.Cook.Tests/bin/Release/net11.0/Fixtures/Shaders/shaders.cook.json
check "only tracked files named exactly shaders.cook.json under the four directories" 0 "tests/ok/shaders.cook.json" "$work" others

repo none README.md src/a.slang
check "no manifest: no output" 0 "" "$work" none
repo empty
check "an empty index: no output" 0 "" "$work" empty

# Failures.
repo newline "tests/a${nl}b/shaders.cook.json"
check "a path with a newline fails" 1 "" "$work" newline
mkdir -p "$work/plain"
check "not a git checkout fails (git's exit code)" 128 "" "$work" plain
check "an empty root fails" 2 "" "$work" ""
check "two arguments fail" 2 "" "$work" all none

if [ "$failures" -ne 0 ]; then
  echo "$failures shader manifest finder check(s) failed."
  exit 1
fi
echo "All shader manifest finder checks passed."
