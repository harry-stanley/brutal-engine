#!/usr/bin/env bash
# Checks that a pack produced exactly the packages the ship gate selects (ADR-0016). CI runs it right after
# "Pack (with package validation)", and check-packages-test.sh is its self-test.
#
# Usage: check-packages.sh <packages-dir> <repo-root>
#
# Expected: every src/<name>/<name>.csproj whose BrutalEngineMilestone is at most src/Directory.Build.props's
# BrutalEngineReleaseMilestone, as src/Directory.Build.targets decides IsPackable. Roslyn components (names ending in
# .Analyzers or .Generators) are skipped, as src/Directory.Build.props skips them; they're never expected. A package id
# is the project name (PackageId is $(MSBuildProjectName)).
# Packed: the ids of the <id>.<version>.nupkg files directly in <packages-dir>; symbol packages (.snupkg) are ignored.
#
# Prints both sets. Exit 0 when they're equal; 1 when they differ (naming the missing and the unexpected ids) or an id
# is packed more than once; 2 on a usage error or input it can't read: a property that isn't set exactly once to a
# non-negative integer of 1 to 9 digits (the form src/Directory.Build.targets and ShipGateRule accept), no engine
# library, or a package file name without a version. The properties are read from the files' text, one
# <Name>value</Name> element each, spelled exactly; the architecture tests (ShipGateTests) reject conditional or
# repeated ones and other spellings, which MSBuild would read too, since its property names are case-insensitive.
# Milestones are decimal (08 is 8, 010 is 10), as MSBuild compares them.
set -eu

readonly MILESTONE='BrutalEngineMilestone'
readonly RELEASE_MILESTONE='BrutalEngineReleaseMilestone'

fail_input() {
  echo "check-packages: $*" >&2
  exit 2
}

if [ $# -ne 2 ]; then
  echo "usage: check-packages.sh <packages-dir> <repo-root>" >&2
  exit 2
fi
packages=$1
root=$2
props="$root/src/Directory.Build.props"
[ -d "$packages" ] || fail_input "$packages isn't a directory."
[ -f "$props" ] || fail_input "$props doesn't exist: <repo-root> must be the repository root."

# read_property <file> <display name> <property>: sets $value to the property's single integer value, or exits 2.
read_property() {
  local file=$1 name=$2 property=$3 matches status count
  set +e
  matches=$(grep -o "<$property>[^<]*</$property>" "$file")
  status=$?
  set -e
  if [ "$status" -gt 1 ]; then
    fail_input "can't read $name."
  elif [ "$status" -eq 1 ]; then
    fail_input "$name doesn't set $property. See ADR-0016."
  fi
  count=$(printf '%s\n' "$matches" | wc -l)
  count=$((count + 0))
  if [ "$count" -ne 1 ]; then
    fail_input "$name sets $property $count times; set it once."
  fi
  value=${matches#"<$property>"}
  value=${value%"</$property>"}
  if ! [[ $value =~ ^[0-9]{1,9}$ ]]; then
    fail_input "$name: $property '$value' isn't a non-negative integer (1 to 9 digits)."
  fi
  value=$((10#$value))
}

read_property "$props" src/Directory.Build.props "$RELEASE_MILESTONE"
release=$value

expected=()
libraries=0
for project in "$root"/src/*/*.csproj; do
  [ -e "$project" ] || continue
  name=$(basename "$project" .csproj)
  case $name in
    *.Analyzers | *.Generators) continue ;;
  esac
  libraries=$((libraries + 1))
  read_property "$project" "src/$(basename "$(dirname "$project")")/$name.csproj" "$MILESTONE"
  if [ "$value" -le "$release" ]; then
    expected+=("$name")
  fi
done
[ "$libraries" -ne 0 ] || fail_input "no engine library (src/<name>/<name>.csproj) under $root/src."

packed=()
for package in "$packages"/*.nupkg; do
  [ -e "$package" ] || continue
  file=$(basename "$package")
  id=$(printf '%s\n' "$file" | sed -E 's/\.[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?\.nupkg$//')
  if [ "$id" = "$file" ] || [ -z "$id" ]; then
    fail_input "can't read a package id and version from $file."
  fi
  packed+=("$id")
done

sorted() {
  if [ $# -ne 0 ]; then
    printf '%s\n' "$@" | LC_ALL=C sort
  fi
}
words() {
  tr '\n' ' ' | sed 's/ $//'
}
expected_list=$(sorted "${expected[@]+"${expected[@]}"}")
packed_list=$(sorted "${packed[@]+"${packed[@]}"}")
echo "Expected ($RELEASE_MILESTONE $release): $(printf '%s' "$expected_list" | words)"
echo "Packed: $(printf '%s' "$packed_list" | words)"

missing=$(LC_ALL=C comm -23 <(printf '%s\n' "$expected_list" | sed '/^$/d') <(printf '%s\n' "$packed_list" | LC_ALL=C sort -u | sed '/^$/d') | words)
unexpected=$(LC_ALL=C comm -13 <(printf '%s\n' "$expected_list" | sed '/^$/d') <(printf '%s\n' "$packed_list" | LC_ALL=C sort -u | sed '/^$/d') | words)
duplicated=$(printf '%s\n' "$packed_list" | sed '/^$/d' | LC_ALL=C uniq -d | words)

status=0
if [ -n "$missing" ]; then
  echo "missing: $missing (the ship gate selects them, but they weren't packed)" >&2
  status=1
fi
if [ -n "$unexpected" ]; then
  echo "unexpected: $unexpected (packed, but the ship gate doesn't select them)" >&2
  status=1
fi
if [ -n "$duplicated" ]; then
  echo "packed more than once: $duplicated" >&2
  status=1
fi
if [ "$status" -ne 0 ]; then
  echo "The packed set differs from the ship gate (ADR-0016): check BrutalEngineMilestone in each csproj and src/Directory.Build.targets." >&2
  exit "$status"
fi
echo "Packed set matches the ship gate: $(printf '%s' "$expected_list" | words)"
