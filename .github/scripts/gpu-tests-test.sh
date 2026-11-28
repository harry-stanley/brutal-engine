#!/usr/bin/env bash
# Self-test for gpu-tests.sh. The tier job runs it before the vulkan-smoke job uses the script, so a broken discovery
# fails loudly instead of silently running no GPU tests. No build and no tests run: the script works over a scratch
# repository tree and a fake dotnet that logs every call and exits with the code a case gives it.
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/gpu-tests.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failures=0

# The fake dotnet: logs its arguments, one call per line, and the crash-report variables it was given (one line per
# call in env, "unset" for a missing one). It exits with the code stored for the project it was given
# (exit-<project directory name>), or 0, and first copies a stored crash report (crash-<project directory name>) into
# its results directory, as the runtime would.
cat > "$work/dotnet" << 'EOF'
#!/usr/bin/env bash
set -euo pipefail
echo "$*" >> "$FAKE_DOTNET_DIR/calls"
echo "${DOTNET_DbgEnableMiniDump-unset} ${DOTNET_EnableCrashReportOnly-unset} ${DOTNET_DbgMiniDumpName-unset}" \
  >> "$FAKE_DOTNET_DIR/env"
project=""
results=""
while [ $# -gt 0 ]; do
  case $1 in
    --project) project=$(basename "$2") ;;
    --results-directory) results=$2 ;;
  esac
  shift
done
if [ -n "$project" ] && [ -f "$FAKE_DOTNET_DIR/crash-$project" ]; then
  cp "$FAKE_DOTNET_DIR/crash-$project" "$results/crash.$project.4242.crashreport.json"
fi
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
  : > "$tree/env"
  case_env=()
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

# crashes <project directory name> <report>: the crash report the fake dotnet leaves for that project.
crashes() {
  printf '%s\n' "$2" > "$tree/crash-$1"
}

# with <NAME=value>...: environment for gpu-tests.sh in this case (GPU_TESTS_RESULTS and GPU_TESTS_OS are otherwise
# unset, whatever the caller's environment holds).
with() {
  case_env+=("$@")
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

# run_script <arguments...>: runs gpu-tests.sh in the case's tree. Sets status, output, calls and envs (the fake
# dotnet's env log).
run_script() {
  status=0
  output=$(cd "$tree/repo" && env -u GPU_TESTS_RESULTS -u GPU_TESTS_OS -u DOTNET_DbgEnableMiniDump \
    -u DOTNET_EnableCrashReportOnly -u DOTNET_DbgMiniDumpName "${case_env[@]}" GPU_TESTS_DOTNET="$work/dotnet" \
    FAKE_DOTNET_DIR="$tree" bash "$script" "$@" 2>&1) || status=$?
  calls=$(cat "$tree/calls")
  envs=$(cat "$tree/env")
}

# contains <text>: true if the case's output contains <text> (several lines, in order), anywhere.
contains() {
  [[ $output == *"$1"* ]]
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

# Evidence (GPU_TESTS_RESULTS): a TRX file and a results directory per project, and on Linux the runtime's crash report.
readonly ALPHA_TRX_CALL="test --project tests/Alpha.Tests -c Release --no-build --results-directory out/gpu/Alpha.Tests --report-trx -- --filter-trait Category=Gpu"
readonly GAMMA_TRX_CALL="test --project tests/Gamma.Tests -c Release --no-build --results-directory out/gpu/Gamma.Tests --report-trx -- --filter-trait Category=Gpu"
fresh; mixed_tree; with GPU_TESTS_RESULTS=out/gpu GPU_TESTS_OS=Linux; run_script Release
expect "each project is run with --report-trx and the results directory" \
  test "$status" -eq 0 -a "$calls" = "$ALPHA_TRX_CALL${nl}$GAMMA_TRX_CALL"
expect "each project's results directory is created" test -d "$tree/repo/out/gpu/Alpha.Tests" -a -d "$tree/repo/out/gpu/Gamma.Tests"
expect "on Linux the runtime writes a crash report, and only that, into the project's results directory" \
  test "$envs" = "1 1 $tree/repo/out/gpu/Alpha.Tests/crash.%e.%p${nl}1 1 $tree/repo/out/gpu/Gamma.Tests/crash.%e.%p"
fresh; mixed_tree; with GPU_TESTS_RESULTS=out/gpu GPU_TESTS_OS=MINGW64_NT-10.0-26100; run_script Release
expect "off Linux each project still gets its TRX file and results directory" \
  test "$status" -eq 0 -a "$calls" = "$ALPHA_TRX_CALL${nl}$GAMMA_TRX_CALL"
expect "off Linux no crash report is asked for" test "$envs" = "unset unset unset${nl}unset unset unset"
fresh; mixed_tree; with GPU_TESTS_OS=Linux; run_script Release
expect "without GPU_TESTS_RESULTS there is no TRX file, results directory or crash report" \
  test "$status" -eq 0 -a "$calls" = "$ALPHA_CALL${nl}$GAMMA_CALL" -a "$envs" = "unset unset unset${nl}unset unset unset"
fresh; mixed_tree; with GPU_TESTS_RESULTS= GPU_TESTS_OS=Linux; run_script Release
expect "an empty GPU_TESTS_RESULTS is the same as none" \
  test "$status" -eq 0 -a "$calls" = "$ALPHA_CALL${nl}$GAMMA_CALL" -a "$envs" = "unset unset unset${nl}unset unset unset"

# Crashed test hosts: exit 128 + the signal without a TRX report, 7 (exited unexpectedly) with one.
fresh; mixed_tree; exits Alpha.Tests 139; run_script Release
expect "an exit 139 from one project is reported with its name and status, the next project still runs, and the script fails" \
  test "$status" -ne 0 -a "$calls" = "$ALPHA_CALL${nl}$GAMMA_CALL" \
  -a "$(grep -cx '::error::The test host of tests/Alpha.Tests crashed with SIGSEGV (exit code 139).' <<< "$output")" -eq 1
fresh; mixed_tree; exits Gamma.Tests 134; run_script Release
expect "an exit 134 is reported as SIGABRT with the project's name and status, and the script fails" \
  test "$status" -ne 0 -a "$calls" = "$ALPHA_CALL${nl}$GAMMA_CALL" \
  -a "$(grep -cx '::error::The test host of tests/Gamma.Tests crashed with SIGABRT (exit code 134).' <<< "$output")" -eq 1
fresh; mixed_tree; exits Alpha.Tests 7; with GPU_TESTS_RESULTS=out/gpu GPU_TESTS_OS=Linux; run_script Release
expect "an exit 7 (the test host exited unexpectedly) fails, and the next project still runs" \
  test "$status" -ne 0 -a "$calls" = "$ALPHA_TRX_CALL${nl}$GAMMA_TRX_CALL"
expect "the exit 7 report names the project, the status and where the evidence is" \
  grep -qx '::error::The test host of tests/Alpha.Tests crashed: it exited unexpectedly (exit code 7). Its TRX file and crash report are in out/gpu/Alpha.Tests.' <<< "$output"
expect "a crash names no crash report when the runtime wrote none" \
  grep -qx 'No crash report in out/gpu/Alpha.Tests.' <<< "$output"

# Crash reports: the runtime's JSON (createdump), trimmed to the fields the summary reads.
readonly CRASH_REPORT='{"payload":{"threads":[
  {"crashed":"false","native_thread_id":"0x10","stack_frames":[
    {"is_managed":"false","native_module":"libc.so.6","unmanaged_name":"pthread_cond_wait","native_image_offset":"0xabc30"},
    {"is_managed":"true","method_name":"Alpha.Tests.Surfaces.SurfaceHelpers.Make()"},
    {"is_managed":"true","method_name":"Alpha.Tests.Surfaces.SurfaceTests.Surface_Created_IsValid()"},
    {"is_managed":"true","method_name":"Xunit.v3.TestRunner.RunTest()"}]},
  {"crashed":"true","native_thread_id":"0x20","stack_frames":[
    {"is_managed":"false","native_module":"libVkLayer_khronos_validation.so","native_image_offset":"0x1774c74"},
    {"is_managed":"false","native_module":"libvulkan.so.1","unmanaged_name":"vkSetDebugUtilsObjectNameEXT","native_image_offset":"0xc818"},
    {"is_managed":"true","native_address":"0x7f3dd358f7ba"},
    {"is_managed":"true","method_name":"Alpha.Tests.Naming.NamingTests.Name_WrongType_IsReported()"}]},
  {"crashed":"false","native_thread_id":"0x30","stack_frames":[
    {"is_managed":"true","method_name":"System.Threading.Thread.StartCallback()"}]}]}}'
readonly IDLE_CRASH_REPORT='{"payload":{"threads":[
  {"crashed":"true","native_thread_id":"0x20","stack_frames":[
    {"is_managed":"false","native_module":"libvulkan_lvp.so","native_image_offset":"0x2ec4ee"}]}]}}'
fresh; mixed_tree; exits Alpha.Tests 7; crashes Alpha.Tests "$CRASH_REPORT"; with GPU_TESTS_RESULTS=out/gpu GPU_TESTS_OS=Linux
run_script Release
expect "a crash report is summarized, and the next project still runs" \
  test "$status" -ne 0 -a "$calls" = "$ALPHA_TRX_CALL${nl}$GAMMA_TRX_CALL"
expect "the summary names the report" \
  grep -qx 'Crash report out/gpu/Alpha.Tests/crash.Alpha.Tests.4242.crashreport.json:' <<< "$output"
expect "the summary prints the crashed thread's stack, native frames by module and symbol or offset" \
  contains "  crashed thread 0x20 (innermost frame first):${nl}    libVkLayer_khronos_validation.so!0x1774c74${nl}    libvulkan.so.1!vkSetDebugUtilsObjectNameEXT${nl}    <managed frame without a name>${nl}    Alpha.Tests.Naming.NamingTests.Name_WrongType_IsReported()${nl}"
expect "the summary names the test method each thread was in, the crashed thread marked" \
  contains "  test methods running at the crash (* the crashed thread):${nl}      0x10 Alpha.Tests.Surfaces.SurfaceTests.Surface_Created_IsValid()${nl}    * 0x20 Alpha.Tests.Naming.NamingTests.Name_WrongType_IsReported()${nl}"
fresh; mixed_tree; exits Alpha.Tests 139; crashes Alpha.Tests "$IDLE_CRASH_REPORT"; with GPU_TESTS_RESULTS=out/gpu GPU_TESTS_OS=Linux
run_script Release
expect "a crash outside every test method says so" \
  grep -qx '    none: the crash came between tests, or at start-up or shutdown' <<< "$output"
fresh; mixed_tree; crashes Alpha.Tests "$CRASH_REPORT"; with GPU_TESTS_RESULTS=out/gpu GPU_TESTS_OS=Linux; run_script Release
expect "a passing project's results directory isn't searched for crash reports" \
  test "$status" -eq 0 -a -n "$output" -a "$(grep -c 'Crash report' <<< "$output")" -eq 0
fresh; mixed_tree; exits Alpha.Tests 7; crashes Alpha.Tests 'not json'; with GPU_TESTS_RESULTS=out/gpu GPU_TESTS_OS=Linux
run_script Release
expect "an unreadable crash report is named, the script still fails and the next project still runs" \
  test "$status" -ne 0 -a "$calls" = "$ALPHA_TRX_CALL${nl}$GAMMA_TRX_CALL"
expect "the unreadable report's note points at the file" \
  grep -qx '  (could not summarize it: read the file itself)' <<< "$output"

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
