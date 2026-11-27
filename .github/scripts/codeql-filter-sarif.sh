#!/usr/bin/env bash
# Removes CodeQL results that misfire on test code from a SARIF file, before it is uploaded to code scanning.
#
# Usage: codeql-filter-sarif.sh <input.sarif> <output.sarif>
#
# A result is removed only when its rule is on TEST_ONLY_RULES below AND every one of its locations is under tests/.
# Everything else is kept: other rules, listed rules with any location outside tests/, and results with no location to
# judge by. The locations are the result's own (SARIF "locations", where code scanning shows the alert), not its
# related locations, which CodeQL uses for context such as the declaration of a type the result mentions, often in
# src/ or a referenced assembly. Nothing else in the file changes. Prints "removed <count> <rule>" per rule.
# Fails closed: a missing input, anything that isn't one SARIF log with at least one run, each with a results array
# (empty is fine), or any jq error exits non-zero and leaves no output file, so the upload step has nothing partial to
# send. An upload with no runs, or a run without results, would close every open alert of the category.
# Self-test: codeql-filter-sarif-test.sh. The CodeQL configuration is described in docs/06-ci-cd-and-release.md
# ("Code scanning"). Keep the two in sync.
set -euo pipefail

# The rules filtered on tests/ only, each for a pattern the project's test rules require or that is harmless in tests.
# They all still run on src/ and samples/, and every rule not listed here keeps its results under tests/.
readonly TEST_ONLY_RULES=(
  # Allocation tests call GC.Collect before the measured window.
  cs/call-to-gc
  # Test worker threads catch every exception and hand it to the test thread: one escaping a thread kills the host.
  cs/catch-of-all-exceptions
  # Dispose-idempotency and ownership tests call Dispose explicitly, often twice.
  cs/dispose-not-called-on-throw
  # Temp-directory tests combine constants, GUIDs and test-controlled names; some rely on the rooted-path behaviour.
  cs/path-combine
  # CodeQL doesn't model the engine's log handler, which formats through IUtf8SpanFormattable, ISpanFormattable or
  # IFormattable.
  cs/call-to-object-tostring
  # Value-type equality tests pass null to Equals(object?) on purpose.
  cs/null-argument-to-equals
  # Test component structs stand in for game components, whose fields are written through refs.
  cs/missed-readonly-modifier
  # Tests reach internals through UnsafeAccessor externs, which CodeQL reads as native code: calls to them...
  cs/call-to-unmanaged-code
  # ...and their declarations.
  cs/unmanaged-code
  # The casts give collection expressions and switch arms the type they need to compile.
  cs/useless-cast-to-self
  # (object) casts give switch arms a common type, or pick the log handler's object overload under test.
  cs/useless-upcast
  # Tests dispose in finally so the leak check after the block, or a use-after-dispose check, sees the disposed object.
  cs/missed-using-statement
)

if [ "$#" -ne 2 ]; then
  echo "usage: codeql-filter-sarif.sh <input.sarif> <output.sarif>" >&2
  exit 64
fi
input=$1
output=$2

# A stale output from an earlier run must never be uploaded in place of this one.
rm -f -- "$output"
if [ ! -f "$input" ]; then
  echo "codeql-filter-sarif: no input file: $input" >&2
  exit 1
fi

listed=$(jq -n -c '$ARGS.positional' --args "${TEST_ONLY_RULES[@]}")

# The definitions both jq passes share. The input is slurped, so a file with no or several JSON documents fails.
readonly DEFINITIONS='
  def checked:
    if length != 1 then error("expected one JSON document, found \(length)") else .[0] end
    | if type != "object" then error("the SARIF log is not an object")
      elif (.runs | type) != "array" then error("the SARIF log has no runs array")
      elif (.runs | length) < 1 then error("the SARIF log has no runs")
      else . end
    | .runs[] |= (
        if type != "object" then error("a run is not an object")
        elif (.results | type) != "array" then error("a run has no results array")
        else . end);
  def under_tests:
    .physicalLocation.artifactLocation.uri | if type == "string" then startswith("tests/") else false end;
  def removable:
    (.ruleId // .rule.id) as $id
    | [(.locations // [])[]] as $locations
    | any($listed[]; . == $id) and ($locations | length > 0) and all($locations[]; under_tests);
'

temporary=$(mktemp "$output.XXXXXX")
trap 'rm -f -- "$temporary"' EXIT

counts=$(jq -r -s --argjson listed "$listed" "$DEFINITIONS"'
  checked
  | [.runs[].results[] | select(removable) | .ruleId // .rule.id]
  | group_by(.)[] | "removed \(length) \(.[0])"' "$input")
jq -c -s --argjson listed "$listed" "$DEFINITIONS"'
  checked
  | .runs[].results |= map(select(removable | not))' "$input" > "$temporary"

# The output must be one SARIF log again before it replaces anything.
jq -e -s 'length == 1 and (.[0].runs | type) == "array" and (.[0].runs | length) >= 1
  and all(.[0].runs[]; (.results | type) == "array")' "$temporary" > /dev/null
mv -- "$temporary" "$output"
trap - EXIT

if [ -n "$counts" ]; then
  echo "$counts"
else
  echo "removed nothing"
fi
