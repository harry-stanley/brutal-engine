#!/usr/bin/env bash
# Runs the tests marked [Trait("Category", "Gpu")]: the tests that need a Vulkan driver. The build-test job has no
# driver and excludes the trait (--filter-not-trait); the vulkan-smoke job sets up lavapipe and runs this script.
#
# Usage, from the repository root:
#   gpu-tests.sh --list            prints the test projects that declare the trait, one per line (nothing if none)
#   gpu-tests.sh <configuration>   runs each one's Gpu tests, already built in that configuration (--no-build)
#
# A test project is a directory tests/*.Tests/ with a .csproj in it. It declares the trait when one of its C# sources
# (bin/ and obj/ excluded) contains Trait("Category", "Gpu") literally, with any whitespace between the tokens. A
# trait spelled any other way (a constant, a custom attribute) isn't found, and its tests would run in neither job.
# Projects anywhere else (src/, tests/TestUtilities/) are never run here.
#
# No project found prints "no GPU-trait tests yet" and succeeds. A project that declares the trait but runs zero Gpu
# tests (Microsoft.Testing.Platform's exit code 8) fails, as does any other non-zero exit. Every project runs, even
# after one has failed. The build-test job's filter has the mirror rule: a project whose every test is Gpu runs zero
# tests there and exits 8, so every test project needs at least one test without the trait.
#
# Inputs (environment):
#   GPU_TESTS_DOTNET  the dotnet executable (default dotnet). The self-test, gpu-tests-test.sh, points it at a fake.
# The trait and the rules are described in docs/05-testing-strategy.md and docs/06-ci-cd-and-release.md.
set -euo pipefail

readonly TRAIT_PATTERN='Trait\([[:space:]]*"Category"[[:space:]]*,[[:space:]]*"Gpu"[[:space:]]*\)'
readonly FILTER='Category=Gpu'
readonly NO_TESTS_RAN=8

dotnet_bin=${GPU_TESTS_DOTNET:-dotnet}

usage() {
  echo "usage: gpu-tests.sh --list | <configuration>" >&2
  exit 64
}
if [ $# -ne 1 ] || [ -z "$1" ] || { [[ $1 == -* ]] && [ "$1" != --list ]; }; then
  usage
fi

# Prints tests/<name>.Tests for every test project whose sources declare the trait. A grep error (an unreadable
# file, say) fails the script rather than silently dropping the project.
list_projects() {
  local dir name found
  for dir in tests/*.Tests/; do
    [ -d "$dir" ] || continue
    name=$(basename "$dir")
    compgen -G "$dir*.csproj" > /dev/null || continue
    found=0
    grep -rqE --include='*.cs' --exclude-dir=bin --exclude-dir=obj "$TRAIT_PATTERN" "$dir" || found=$?
    case $found in
      0) echo "tests/$name" ;;
      1) ;;
      *)
        echo "gpu-tests.sh: could not search tests/$name (grep exit code $found)" >&2
        return 1
        ;;
    esac
  done
}

projects=$(list_projects)
if [ "$1" = --list ]; then
  [ -z "$projects" ] || printf '%s\n' "$projects"
  exit 0
fi
if [ -z "$projects" ]; then
  echo "no GPU-trait tests yet"
  exit 0
fi

configuration=$1
echo "Test projects that declare the Gpu trait:"
printf '%s\n' "$projects" | sed 's/^/  /'
failed=0
while read -r project; do
  echo "Running the Gpu tests of $project ($configuration)"
  status=0
  "$dotnet_bin" test --project "$project" -c "$configuration" --no-build -- --filter-trait "$FILTER" || status=$?
  case $status in
    0) ;;
    "$NO_TESTS_RAN")
      echo "::error::$project declares the Gpu trait but ran no Gpu tests (exit code $NO_TESTS_RAN)."
      failed=1
      ;;
    *)
      echo "::error::The Gpu tests of $project failed (exit code $status)."
      failed=1
      ;;
  esac
done <<< "$projects"
if [ "$failed" -ne 0 ]; then
  exit 1
fi
echo "Every Gpu test project passed."
