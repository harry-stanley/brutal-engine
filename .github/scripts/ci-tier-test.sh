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

# Docs tier: nothing that is built, tested or packed changed.
check docs "docs folder" "docs/06-ci-cd-and-release.md${nl}docs/spikes/README.md"
check docs "community files" "CONTRIBUTING.md${nl}CHANGELOG.md${nl}SECURITY.md${nl}CODE_OF_CONDUCT.md"
check docs "GitHub templates and owners" ".github/ISSUE_TEMPLATE/bug_report.yml${nl}.github/pull_request_template.md${nl}.github/CODEOWNERS"
check docs "name only starts like a backend" "docs/src/BrutalEngine.Graphics/notes.md"
check docs "docs page about samples" "docs/samples/HelloWindow.md"

# Fast tier: no path can affect native interop, NativeAOT or GPU tests.
check fast "README is packed into every package" "docs/06-ci-cd-and-release.md${nl}README.md"
check fast "NOTICE is packed into every package" "NOTICE.txt"
# These three used Core paths until Core joined the full tier (the smoke harness uses it); World keeps their intent.
check fast "markdown inside a project" "src/BrutalEngine.World/README.md"
check fast "docs plus code" "docs/a.md${nl}src/BrutalEngine.World/EcsWorld.cs"
check fast "Core tests only (the harness doesn't use them)" "tests/BrutalEngine.Core.Tests/Memory/HeapAllocatorTests.cs"
check fast "analyzers" "src/BrutalEngine.Analyzers/DiscardedStatusResultAnalyzer.cs"
check fast "other workflows" ".github/workflows/codeql.yml${nl}.github/dependabot.yml"
check fast "lock file outside the smoke harness" "tests/BrutalEngine.Core.Tests/packages.lock.json"
check fast "ECS world (no native code)" "src/BrutalEngine.World/EcsWorld.cs"
check fast "ECS world tests" "tests/BrutalEngine.World.Tests/EntityTests.cs"
check fast "shared test utilities" "tests/TestUtilities/FakeGraphicsBackend.cs"

# Full tier.
check full "empty list" ""
check full "smoke harness" "tests/BrutalEngine.Aot.Smoke/VulkanOffscreenScenario.cs"
check full "Core (used by the smoke harness)" "src/BrutalEngine.Core/Logging/Logger.cs"
check full "Core code and tests" "src/BrutalEngine.Core/Memory/HeapAllocator.cs${nl}tests/BrutalEngine.Core.Tests/Memory/HeapAllocatorTests.cs"
check full "Vulkan backend" "src/BrutalEngine.Graphics.Vulkan/Device.cs"
check full "platform abstraction" "src/BrutalEngine.Platform/IWindow.cs"
check full "audio backend" "src/BrutalEngine.Audio.Sdl3/Mixer.cs"
check full "runtime" "src/BrutalEngine.Runtime/Game.cs"
check full "SDL3 platform backend" "src/BrutalEngine.Platform.Sdl3/SdlPlatform.cs"
check full "SDL3 platform backend tests (Gpu trait)" "tests/BrutalEngine.Platform.Sdl3.Tests/SdlWindowTests.cs"
check full "Vulkan backend tests (Gpu trait)" "tests/BrutalEngine.Graphics.Vulkan.Tests/LoaderSetupTests.cs"
check full "CI workflow" ".github/workflows/ci.yml"
check full "pinned natives" "natives/macos-vulkan/pins.env"
check full "composite action" ".github/actions/setup-software-vulkan/action.yml"
check full "tier script" ".github/scripts/ci-tier.sh"
check full "central package versions" "Directory.Packages.props"
check full "nested build props" "tests/Directory.Build.props"
check full "SDK pin" "global.json"
check full "one native path among docs" "docs/a.md${nl}src/BrutalEngine.Graphics/Renderer.cs${nl}docs/b.md"
check full "sample code" "samples/HelloWindow/Program.cs"
check full "sample README (no docs carve-out for samples)" "samples/HelloWindow/README.md"
check full "sample next to docs" "docs/a.md${nl}samples/HelloWindow/HelloWindow.csproj"

if [ "$failures" -ne 0 ]; then
  echo "$failures tier rule check(s) failed."
  exit 1
fi
echo "All tier rule checks passed."
