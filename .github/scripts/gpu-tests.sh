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
# A test host that crashes is reported as a crash, with the project's name: exit code 128 + N is signal N (139 SIGSEGV,
# 134 SIGABRT), and with a TRX report Microsoft.Testing.Platform turns any crash into exit code 7 (the test host exited
# unexpectedly).
#
# Inputs (environment):
#   GPU_TESTS_DOTNET   the dotnet executable (default dotnet). The self-test, gpu-tests-test.sh, points it at a fake.
#   GPU_TESTS_RESULTS  a directory for each run's evidence (unset or empty: none). Each project then writes a TRX report
#                      of its finished tests into <directory>/<project name>/ (--results-directory, --report-trx). On
#                      Linux, a crashing test host also writes the .NET runtime's crash report there: the managed and
#                      native stacks of every thread, as JSON (createdump; DOTNET_DbgEnableMiniDump=1 with
#                      DOTNET_EnableCrashReportOnly=1, so no dump file). After a failed run, the log gets a summary of
#                      each crash report: the crashed thread's stack, and the test method each thread was in.
#   GPU_TESTS_OS       the OS name (default: uname -s); crash reports are asked for only on Linux. For the self-test.
# The trait and the rules are described in docs/05-testing-strategy.md and docs/06-ci-cd-and-release.md.
set -euo pipefail

readonly TRAIT_PATTERN='Trait\([[:space:]]*"Category"[[:space:]]*,[[:space:]]*"Gpu"[[:space:]]*\)'
readonly FILTER='Category=Gpu'
readonly NO_TESTS_RAN=8
readonly HOST_EXITED_UNEXPECTEDLY=7

# The crash report summary: the crashed thread's frames (native ones as module!symbol, or module!offset in the image),
# then every thread that is inside a test method (a frame in a *.Tests.* namespace; the outermost such frame names it).
readonly CRASH_SUMMARY='
def frame: if .is_managed == "true" then (.method_name // "<managed frame without a name>")
  else "\(.native_module // "?")!\(.unmanaged_name // .native_image_offset // "?")" end;
def test_method: [.stack_frames[] | (.method_name // "") | select(test("[.]Tests[.]"))] | last;
.payload.threads as $threads
| ($threads[] | select(.crashed == "true") | "crashed thread \(.native_thread_id) (innermost frame first):",
    (.stack_frames[] | "  " + frame)),
  "test methods running at the crash (* the crashed thread):",
  ([$threads[] | select(test_method != null)
    | "  \(if .crashed == "true" then "*" else " " end) \(.native_thread_id) \(test_method)"]
   | if length == 0 then ["  none: the crash came between tests, or at start-up or shutdown"] else . end | .[])'

dotnet_bin=${GPU_TESTS_DOTNET:-dotnet}
results_root=${GPU_TESTS_RESULTS:-}
os=${GPU_TESTS_OS:-$(uname -s)}

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

# summarize_crashes <directory>: prints a summary of every crash report in a project's results directory.
summarize_crashes() {
  local report found=0
  for report in "$1"/crash.*.crashreport.json; do
    [ -f "$report" ] || continue
    found=1
    echo "Crash report $report:"
    jq -r "$CRASH_SUMMARY" "$report" 2> /dev/null | sed 's/^/  /' \
      || echo "  (could not summarize it: read the file itself)"
  done
  if [ "$found" -eq 0 ]; then
    echo "No crash report in $1."
  fi
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
  results=()
  crash_env=()
  dir=""
  if [ -n "$results_root" ]; then
    dir="$results_root/$(basename "$project")"
    mkdir -p "$dir"
    results=(--results-directory "$dir" --report-trx)
    if [ "$os" = Linux ]; then
      crash_env=(DOTNET_DbgEnableMiniDump=1 DOTNET_EnableCrashReportOnly=1 "DOTNET_DbgMiniDumpName=$(cd "$dir" && pwd)/crash.%e.%p")
    fi
  fi
  status=0
  env "${crash_env[@]}" "$dotnet_bin" test --project "$project" -c "$configuration" --no-build "${results[@]}" \
    -- --filter-trait "$FILTER" || status=$?
  case $status in
    0) ;;
    "$NO_TESTS_RAN")
      echo "::error::$project declares the Gpu trait but ran no Gpu tests (exit code $NO_TESTS_RAN)."
      ;;
    "$HOST_EXITED_UNEXPECTEDLY")
      echo "::error::The test host of $project crashed: it exited unexpectedly (exit code $status).${dir:+ Its TRX file and crash report are in $dir.}"
      ;;
    129 | 1[3-8][0-9] | 19[0-2])
      echo "::error::The test host of $project crashed with SIG$(kill -l "$((status - 128))") (exit code $status)."
      ;;
    *)
      echo "::error::The Gpu tests of $project failed (exit code $status)."
      ;;
  esac
  if [ "$status" -ne 0 ]; then
    failed=1
    if [ -n "$dir" ]; then
      summarize_crashes "$dir"
    fi
  fi
done <<< "$projects"
if [ "$failed" -ne 0 ]; then
  exit 1
fi
echo "Every Gpu test project passed."
