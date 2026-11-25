#!/usr/bin/env bash
# Self-test for ci-tier.sh. CI runs it before selecting a tier, so a broken rule fails loudly
# instead of silently skipping the full tier.
set -euo pipefail

script="$(dirname "$0")/ci-tier.sh"
failures=0

check() {
  local expected=$1 description=$2 paths=$3 actual
  actual=$(printf '%s' "$paths" | bash "$script")
  if [ "$actual" = "$expected" ]; then
    echo "ok    $expected  $description"
  else
    echo "FAIL  expected $expected, got $actual: $description"
    failures=$((failures + 1))
  fi
}

nl=$'\n'

# Fast tier: no path can affect native interop, NativeAOT or GPU tests.
check fast "docs only" "docs/06-ci-cd-and-release.md${nl}README.md"
check fast "Core code and tests" "src/BrutalEngine.Core/Memory/HeapAllocator.cs${nl}tests/BrutalEngine.Core.Tests/Memory/HeapAllocatorTests.cs"
check fast "analyzers" "src/BrutalEngine.Analyzers/DiscardedStatusResultAnalyzer.cs"
check fast "other workflows" ".github/workflows/codeql.yml${nl}.github/dependabot.yml"
check fast "lock file outside the smoke harness" "tests/BrutalEngine.Core.Tests/packages.lock.json"
check fast "name only starts like a backend" "docs/src/BrutalEngine.Graphics/notes.md"

# Full tier.
check full "empty list" ""
check full "smoke harness" "tests/BrutalEngine.Aot.Smoke/VulkanOffscreenScenario.cs"
check full "Vulkan backend" "src/BrutalEngine.Graphics.Vulkan/Device.cs"
check full "platform abstraction" "src/BrutalEngine.Platform/IWindow.cs"
check full "audio backend" "src/BrutalEngine.Audio.Sdl3/Mixer.cs"
check full "runtime" "src/BrutalEngine.Runtime/Game.cs"
check full "CI workflow" ".github/workflows/ci.yml"
check full "composite action" ".github/actions/setup-software-vulkan/action.yml"
check full "tier script" ".github/scripts/ci-tier.sh"
check full "central package versions" "Directory.Packages.props"
check full "nested build props" "tests/Directory.Build.props"
check full "SDK pin" "global.json"
check full "one native path among docs" "docs/a.md${nl}src/BrutalEngine.Graphics/Renderer.cs${nl}docs/b.md"

if [ "$failures" -ne 0 ]; then
  echo "$failures tier rule check(s) failed."
  exit 1
fi
echo "All tier rule checks passed."
