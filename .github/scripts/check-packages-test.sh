#!/usr/bin/env bash
# Self-test for check-packages.sh. The tier job runs it, so a broken ship-gate check fails loudly instead of letting a
# wrong package set through. No build and no pack run: each case is a scratch repository tree (project files and
# src/Directory.Build.props) and a packages directory of empty .nupkg files, named as dotnet pack names them.
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/check-packages.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failures=0
case_number=0

# Starts a case: an empty repository tree and packages directory.
fresh() {
  tree="$work/case$((++case_number))"
  mkdir -p "$tree/repo/src" "$tree/packages"
}

# release <text>: src/Directory.Build.props with <text> as the body of its property group.
release() {
  printf '<Project>\n  <PropertyGroup>\n    %s\n  </PropertyGroup>\n</Project>\n' "$1" > "$tree/repo/src/Directory.Build.props"
}

# library <name> <property group body>: src/<name>/<name>.csproj.
library() {
  mkdir -p "$tree/repo/src/$1"
  printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    %s\n  </PropertyGroup>\n</Project>\n' "$2" \
    > "$tree/repo/src/$1/$1.csproj"
}

# packed <file>...: empty package files in the packages directory.
packed() {
  for file in "$@"; do
    : > "$tree/packages/$file"
  done
}

# The repository every passing case starts from: two milestone-1 libraries, Core at 0 and one milestone-2 library, with
# release milestone 1, and the three packages the gate selects.
standard_tree() {
  release '<BrutalEngineReleaseMilestone>1</BrutalEngineReleaseMilestone>'
  library Fixture.Core '<BrutalEngineMilestone>0</BrutalEngineMilestone>'
  library Fixture.Platform '<BrutalEngineMilestone>1</BrutalEngineMilestone>'
  library Fixture.Platform.Sdl3 '<Description>x</Description>
    <BrutalEngineMilestone>1</BrutalEngineMilestone>'
  library Fixture.Next '<BrutalEngineMilestone>2</BrutalEngineMilestone>'
  packed Fixture.Core.0.2.0-alpha.0.84.nupkg Fixture.Platform.0.2.0-alpha.0.84.nupkg Fixture.Platform.Sdl3.0.2.0-alpha.0.84.nupkg
}

# check <expected exit code> <description> <text the output must contain>: runs the script on the case's packages
# directory and repository root, and compares the exit code and output.
check() {
  check_with "$1" "$2" "$3" "$tree/packages" "$tree/repo"
}

# check_with <expected exit code> <description> <text> [arguments]: the same, with exactly the arguments given.
check_with() {
  local expected=$1 description=$2 text=$3 output code
  shift 3
  set +e
  output=$(bash "$script" "$@" 2>&1)
  code=$?
  set -e
  if [ "$code" -ne "$expected" ]; then
    echo "FAIL  $description: expected exit $expected, got $code. Output:"
    printf '%s\n' "$output" | sed 's/^/        /'
    failures=$((failures + 1))
  elif [[ $output != *"$text"* ]]; then
    echo "FAIL  $description: exit $code, but the output doesn't contain \"$text\". Output:"
    printf '%s\n' "$output" | sed 's/^/        /'
    failures=$((failures + 1))
  else
    echo "ok    exit $code  $description"
  fi
}

# Passing cases.
fresh; standard_tree
check 0 "exact set passes" "Packed set matches the ship gate: Fixture.Core Fixture.Platform Fixture.Platform.Sdl3"

fresh; standard_tree
packed Fixture.Core.0.2.0-alpha.0.84.snupkg Fixture.Next.0.2.0-alpha.0.84.snupkg
check 0 ".snupkg ignored" "Packed set matches"

fresh; standard_tree
library Fixture.Analyzers '<TargetFramework>netstandard2.0</TargetFramework>'
library Fixture.Generators '<BrutalEngineMilestone>9</BrutalEngineMilestone>'
check 0 "Roslyn components ignored (no milestone needed, none expected)" "Packed set matches"

fresh; standard_tree
release '<BrutalEngineReleaseMilestone>2</BrutalEngineReleaseMilestone>'
packed Fixture.Next.0.3.0.nupkg
check 0 "release 2 includes the milestone-2 library" "Fixture.Core Fixture.Next Fixture.Platform Fixture.Platform.Sdl3"

fresh; standard_tree
release '<BrutalEngineReleaseMilestone>0</BrutalEngineReleaseMilestone>'
rm "$tree/packages/"*
packed Fixture.Core.1.0.0+build.5.nupkg
check 0 "release 0 is Core only; build metadata in the version" "Packed set matches the ship gate: Fixture.Core"

fresh; standard_tree
library Fixture.Platform '<BrutalEngineMilestone>01</BrutalEngineMilestone>'
check 0 "leading zero is still milestone 1" "Packed set matches"

# Set differences: exit 1.
fresh; standard_tree
rm "$tree/packages/Fixture.Platform.0.2.0-alpha.0.84.nupkg"
check 1 "missing id fails" "missing: Fixture.Platform"

fresh; standard_tree
packed Fixture.Next.0.2.0-alpha.0.84.nupkg
check 1 "unexpected id fails" "unexpected: Fixture.Next"

fresh; standard_tree
packed Fixture.Analyzers.0.2.0-alpha.0.84.nupkg
check 1 "a packed Roslyn component is unexpected" "unexpected: Fixture.Analyzers"

fresh; standard_tree
rm "$tree/packages/Fixture.Core.0.2.0-alpha.0.84.nupkg"
packed Fixture.Next.0.2.0-alpha.0.84.nupkg
check 1 "missing and unexpected both named (missing)" "missing: Fixture.Core"
check 1 "missing and unexpected both named (unexpected)" "unexpected: Fixture.Next"

fresh; standard_tree
packed Fixture.Core.0.1.0.nupkg
check 1 "an id packed twice fails" "packed more than once: Fixture.Core"

fresh; standard_tree
rm "$tree/packages/"*
check 1 "empty packages directory fails" "missing: Fixture.Core Fixture.Platform Fixture.Platform.Sdl3"

# Bad input: exit 2.
fresh; standard_tree
release '<Description>no release milestone</Description>'
check 2 "missing release property exits 2" "BrutalEngineReleaseMilestone"

fresh; standard_tree
release '<BrutalEngineReleaseMilestone>one</BrutalEngineReleaseMilestone>'
check 2 "non-integer release property exits 2" "isn't a non-negative integer"

fresh; standard_tree
library Fixture.Platform '<Description>no milestone</Description>'
check 2 "library without a milestone exits 2" "src/Fixture.Platform/Fixture.Platform.csproj doesn't set BrutalEngineMilestone"

fresh; standard_tree
library Fixture.Platform '<BrutalEngineMilestone>1.5</BrutalEngineMilestone>'
check 2 "non-integer milestone exits 2" "src/Fixture.Platform/Fixture.Platform.csproj: BrutalEngineMilestone '1.5' isn't a non-negative integer"

fresh; standard_tree
library Fixture.Platform '<BrutalEngineMilestone>1000000000</BrutalEngineMilestone>'
check 2 "ten-digit milestone exits 2" "isn't a non-negative integer"

fresh; standard_tree
library Fixture.Platform '<BrutalEngineMilestone>1</BrutalEngineMilestone>
    <BrutalEngineMilestone>2</BrutalEngineMilestone>'
check 2 "milestone set twice exits 2" "sets BrutalEngineMilestone 2 times"

fresh; standard_tree
packed Fixture.Core.nupkg
check 2 "package file without a version exits 2" "can't read a package id and version from Fixture.Core.nupkg"

fresh; standard_tree
rm -r "$tree/repo/src/"Fixture.*
check 2 "no engine library exits 2" "no engine library"

fresh; standard_tree
check_with 2 "no arguments exits 2" "usage: check-packages.sh <packages-dir> <repo-root>"
check_with 2 "one argument exits 2" "usage: check-packages.sh" "$tree/packages"
check_with 2 "three arguments exit 2" "usage: check-packages.sh" "$tree/packages" "$tree/repo" extra
check_with 2 "packages directory that doesn't exist exits 2" "isn't a directory" "$tree/missing" "$tree/repo"
check_with 2 "repository root without src/Directory.Build.props exits 2" "src/Directory.Build.props" "$tree/packages" "$tree/packages"

if [ "$failures" -ne 0 ]; then
  echo "$failures package check case(s) failed."
  exit 1
fi
echo "All package check cases passed."
