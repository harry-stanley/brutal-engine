#!/usr/bin/env bash
# Self-test for gpu-tests.sh. The tier job runs it before the vulkan-smoke job uses the script, so a broken discovery
# fails loudly instead of silently running no GPU tests. No build and no tests run: the script works over a scratch
# repository tree and a fake dotnet that logs every call and exits with the code a case gives it.
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/gpu-tests.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failures=0

# The fake dotnet: logs its arguments, one call per line, and exits with the code stored for the project it was
# given (exit-<project directory name>), or 0.
cat > "$work/dotnet" << 'EOF'
#!/usr/bin/env bash
set -euo pipefail
echo "$*" >> "$FAKE_DOTNET_DIR/calls"
project=""
while [ $# -gt 0 ]; do
  if [ "$1" = --project ]; then
    project=$(basename "$2")
  fi
  shift
done
code=0
if [ -n "$project" ] && [ -f "$FAKE_DOTNET_DIR/exit-$project" ]; then
  code=$(cat "$FAKE_DOTNET_DIR/exit-$project")
fi
exit "$code"
EOF
chmod +x "$work/dotnet"

# Starts a case with an empty repository tree.
fresh() {
  tree="$work/case$((++case_number))"
  mkdir -p "$tree/repo"
  : > "$tree/calls"
}
case_number=0

# source <path in the repository> <content>: writes a file into the case's tree.
source_file() {
  mkdir -p "$(dirname "$tree/repo/$1")"
  printf '%s\n' "$2" > "$tree/repo/$1"
}

# project <directory>: an SDK-style project file named after its directory.
project() {
  source_file "$1/$(basename "$1").csproj" '<Project Sdk="Microsoft.NET.Sdk" />'
}

exits() {
  printf '%s' "$2" > "$tree/exit-$1"
}

readonly GPU_TEST='    [Fact]
    [Trait("Category", "Gpu")]
    public void Surface_Created_IsValid() { }'

# A tree with every kind of project the discovery must tell apart.
mixed_tree() {
  project tests/Alpha.Tests
  source_file tests/Alpha.Tests/Surfaces/SurfaceTests.cs "$GPU_TEST"
  project tests/Beta.Tests
  source_file tests/Beta.Tests/BetaTests.cs '    [Fact]
    [Trait("Category", "Slow")]
    public void Slow_Runs() { }'
  # Build output and other file types are not sources.
  source_file tests/Beta.Tests/obj/Release/Generated.cs "$GPU_TEST"
  source_file tests/Beta.Tests/bin/Release/Copied.cs "$GPU_TEST"
  source_file tests/Beta.Tests/README.md 'Mark GPU tests with [Trait("Category", "Gpu")].'
  project tests/Gamma.Tests
  source_file tests/Gamma.Tests/GammaTests.cs '[Trait( "Category" ,  "Gpu" )] public sealed class GammaTests { }'
  # Not test projects: src/ projects, test helpers outside tests/*.Tests/, a *.Tests folder without a project file.
  project src/Delta
  source_file src/Delta/Delta.cs "$GPU_TEST"
  project tests/TestUtilities
  source_file tests/TestUtilities/GpuFixture.cs "$GPU_TEST"
  source_file tests/Epsilon.Tests/EpsilonTests.cs "$GPU_TEST"
}

# run_script <arguments...>: runs gpu-tests.sh in the case's tree. Sets status, output and calls.
run_script() {
  status=0
  output=$(cd "$tree/repo" && GPU_TESTS_DOTNET="$work/dotnet" FAKE_DOTNET_DIR="$tree" bash "$script" "$@" 2>&1) \
    || status=$?
  calls=$(cat "$tree/calls")
}

# expect <description> <condition...>: one check, passing if the condition (a command) succeeds.
expect() {
  local description=$1
  shift
  if "$@"; then
    echo "ok    $description"
  else
    echo "FAIL  $description"
    echo "      exit $status; output:"
    printf '%s\n' "$output" | sed 's/^/        /'
    echo "      dotnet calls:"
    printf '%s\n' "$calls" | sed 's/^/        /'
    failures=$((failures + 1))
  fi
}

nl=$'\n'
readonly ALPHA_CALL="test --project tests/Alpha.Tests -c Release --no-build -- --filter-trait Category=Gpu"
readonly GAMMA_CALL="test --project tests/Gamma.Tests -c Release --no-build -- --filter-trait Category=Gpu"

# Discovery.
fresh; mixed_tree; run_script --list
expect "trait in a test project found; non-test project ignored (--list)" \
  test "$status" -eq 0 -a "$output" = "tests/Alpha.Tests${nl}tests/Gamma.Tests" -a -z "$calls"

# Running.
fresh; mixed_tree; run_script Release
expect "trait in a test project found: each one's Gpu tests run, and nothing else" \
  test "$status" -eq 0 -a "$calls" = "$ALPHA_CALL${nl}$GAMMA_CALL"
fresh; mixed_tree; run_script Debug
expect "the configuration is passed through" \
  grep -qx -- "test --project tests/Alpha.Tests -c Debug --no-build -- --filter-trait Category=Gpu" <<< "$calls"
fresh; mixed_tree; exits Alpha.Tests 8; run_script Release
expect "a project that declares the trait but runs zero Gpu tests (exit 8) fails" \
  test "$status" -ne 0 -a "$calls" = "$ALPHA_CALL${nl}$GAMMA_CALL"
expect "the zero-tests failure names the project" grep -q 'tests/Alpha.Tests declares the Gpu trait but ran no' <<< "$output"
fresh; mixed_tree; exits Gamma.Tests 2; run_script Release
expect "a failing Gpu test fails the script" \
  test "$status" -ne 0 -a "$calls" = "$ALPHA_CALL${nl}$GAMMA_CALL"
fresh; mixed_tree; exits Alpha.Tests 1; run_script Release
expect "the next project still runs after one fails" \
  test "$status" -ne 0 -a "$calls" = "$ALPHA_CALL${nl}$GAMMA_CALL"

# None found.
fresh; project tests/Beta.Tests; source_file tests/Beta.Tests/BetaTests.cs '[Fact] public void A() { }'
project src/Delta; source_file src/Delta/Delta.cs "$GPU_TEST"
run_script Release
expect "none found succeeds" test "$status" -eq 0 -a "$output" = "no GPU-trait tests yet" -a -z "$calls"
run_script --list
expect "none found lists nothing" test "$status" -eq 0 -a -z "$output" -a -z "$calls"
fresh; run_script Release
expect "no tests directory at all succeeds" test "$status" -eq 0 -a "$output" = "no GPU-trait tests yet" -a -z "$calls"

# Usage errors (64) never run anything.
fresh; mixed_tree; run_script
expect "no argument is a usage error" test "$status" -eq 64 -a -z "$calls"
run_script Release extra
expect "two arguments are a usage error" test "$status" -eq 64 -a -z "$calls"
run_script --bogus
expect "an unknown option is a usage error" test "$status" -eq 64 -a -z "$calls"

if [ "$failures" -ne 0 ]; then
  echo "$failures GPU-trait discovery check(s) failed."
  exit 1
fi
echo "All GPU-trait discovery checks passed."
