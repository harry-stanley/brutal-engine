#!/usr/bin/env bash
# Picks the CI tier for a pull request from its changed paths (one per line on stdin).
#
# Prints "full" if any path can affect native interop, NativeAOT or the GPU smoke tests, "docs" if every path is
# documentation that nothing builds, tests or packs, and otherwise "fast".
# Empty input prints "full": an empty list can't prove the change is safe to skip.
# The tiers are described in docs/06-ci-cd-and-release.md ("CI tiers"). Keep the two in sync.
set -euo pipefail

# Extend this when a module starts touching native code, or the smoke harness starts using a module. Samples are
# run on every OS by the vulkan-smoke job, so any change under samples/ (README included) gets the full tier.
readonly FULL_TIER_PATHS='^(src/BrutalEngine\.(Platform|Graphics|Audio|Runtime)[^/]*/|tests/BrutalEngine\.Aot\.Smoke/|samples/|natives/|\.github/(actions|scripts)/|\.github/workflows/ci\.yml$|global\.json$|[Nn]u[Gg]et\.config$|(.*/)?Directory\.(Build|Packages)\.(props|targets)$)'

# Documentation only. README.md and NOTICE.txt are not here: every package includes them (src/Directory.Build.props).
readonly DOCS_TIER_PATHS='^(docs/|(CHANGELOG|CONTRIBUTING|CODE_OF_CONDUCT|SECURITY)\.md$|\.github/(ISSUE_TEMPLATE/|pull_request_template\.md$|CODEOWNERS$))'

paths=$(cat)
if [ -z "$paths" ]; then
  echo full
elif grep -Eq "$FULL_TIER_PATHS" <<< "$paths"; then
  echo full
elif ! grep -Evq "$DOCS_TIER_PATHS" <<< "$paths"; then
  echo docs
else
  echo fast
fi
