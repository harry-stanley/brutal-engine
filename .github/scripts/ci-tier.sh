#!/usr/bin/env bash
# Picks the CI tier for a pull request from its changed paths (one per line on stdin).
#
# Prints "full" if any path can affect native interop, NativeAOT or the GPU smoke tests, otherwise "fast".
# Empty input prints "full": an empty list can't prove the change is safe to skip.
# The tiers are described in docs/06-ci-cd-and-release.md ("CI tiers"). Keep the two in sync.
set -euo pipefail

# Extend this when a module starts touching native code, or the smoke harness starts using a module.
readonly FULL_TIER_PATHS='^(src/BrutalEngine\.(Platform|Graphics|Audio|Runtime)[^/]*/|tests/BrutalEngine\.Aot\.Smoke/|\.github/(actions|scripts)/|\.github/workflows/ci\.yml$|global\.json$|[Nn]u[Gg]et\.config$|(.*/)?Directory\.(Build|Packages)\.(props|targets)$)'

paths=$(cat)
if [ -z "$paths" ]; then
  echo full
elif grep -Eq "$FULL_TIER_PATHS" <<< "$paths"; then
  echo full
else
  echo fast
fi
