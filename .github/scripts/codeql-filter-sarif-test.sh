#!/usr/bin/env bash
# Self-test for codeql-filter-sarif.sh. The CodeQL workflow runs it before it filters, so a broken filter fails the job
# instead of uploading results it shouldn't have dropped (or dropping results it should have kept). Needs bash and jq.
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/codeql-filter-sarif.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failures=0
case_number=0

# The rules the filter must drop under tests/, written out here rather than read from the script, so a rule added to or
# removed from its list fails this test until the test is updated with it.
readonly EXPECTED_RULES=(
  cs/call-to-gc
  cs/catch-of-all-exceptions
  cs/dispose-not-called-on-throw
  cs/path-combine
  cs/call-to-object-tostring
  cs/null-argument-to-equals
  cs/missed-readonly-modifier
  cs/call-to-unmanaged-code
  cs/unmanaged-code
  cs/useless-cast-to-self
  cs/useless-upcast
  cs/missed-using-statement
)

# result <rule> <uri>...: a compact SARIF result for <rule> with one primary location per <uri> (none if no uri).
result() {
  local rule=$1
  shift
  jq -n -c --arg rule "$rule" '{
    ruleId: $rule,
    rule: {id: $rule, index: 0},
    message: {text: "m"},
    locations: [$ARGS.positional[] | {physicalLocation: {artifactLocation: {uri: ., uriBaseId: "%SRCROOT%", index: 0},
      region: {startLine: 3, startColumn: 9}}}]
  }' --args "$@"
}

# related <result> <uri>...: the result with a related location per <uri>.
related() {
  local base=$1
  shift
  jq -c '. + {relatedLocations: [$ARGS.positional[] | {id: 1,
    physicalLocation: {artifactLocation: {uri: ., uriBaseId: "%SRCROOT%"}}, message: {text: "r"}}]}' \
    --args "$@" <<< "$base"
}

# run_of <result>...: a compact SARIF run of the CodeQL tool, with run metadata, holding the results.
run_of() {
  jq -n -c '{
    tool: {driver: {name: "CodeQL", semanticVersion: "2.23.5",
      rules: [{id: "cs/call-to-gc", properties: {precision: "very-high", tags: ["maintainability"]}}]}},
    invocations: [{executionSuccessful: true, toolExecutionNotifications: []}],
    artifacts: [{location: {uri: "tests/A.cs", uriBaseId: "%SRCROOT%", index: 0}}],
    automationDetails: {id: "/language:csharp/"},
    columnKind: "utf16CodeUnits",
    properties: {"semmle.formatSpecifier": "sarif-latest", big: $big},
    results: $ARGS.positional
  }' --argjson big 12345678901234567890123 --jsonargs "$@"
}

# sarif <run>...: a compact SARIF log holding the runs.
sarif() {
  jq -n -c '{"$schema": "https://json.schemastore.org/sarif-2.1.0.json", version: "2.1.0", runs: $ARGS.positional}' \
    --jsonargs "$@"
}

# fresh: starts a case with input file $in and output path $out.
fresh() {
  case_number=$((case_number + 1))
  in="$work/case$case_number.sarif"
  out="$work/case$case_number.filtered.sarif"
}

# filter: runs the script on $in into $out, recording the exit code in $code and the output in $log.
filter() {
  set +e
  log=$(bash "$script" "$in" "$out" 2>&1)
  code=$?
  set -e
}

pass() {
  echo "ok    $1"
}

fail() {
  echo "FAIL  $1: $2"
  [ -z "${log:-}" ] || sed 's/^/        /' <<< "$log"
  failures=$((failures + 1))
}

# expect_results <description> <jq filter over the output>: the script succeeded, and <filter> (one result per line,
# compact) prints exactly the expected text from stdin.
expect_results() {
  local description=$1 query=$2 expected actual
  expected=$(cat)
  filter
  if [ "$code" -ne 0 ]; then
    fail "$description" "exit $code, expected 0"
  elif [ ! -s "$out" ]; then
    fail "$description" "no output file"
  elif ! actual=$(jq -c "$query" "$out" 2>&1); then
    fail "$description" "output isn't JSON: $actual"
  elif [ "$actual" != "$expected" ]; then
    fail "$description" "expected$(printf '\n%s' "$expected" | sed 's/^/  /')$(printf '\ngot\n')$(sed 's/^/  /' <<< "$actual")"
  else
    pass "$description"
  fi
}

# expect_failure <description>: the script exited non-zero and left no output file behind.
expect_failure() {
  local description=$1
  : > "$out.stale"
  mv "$out.stale" "$out"
  filter
  if [ "$code" -eq 0 ]; then
    fail "$description" "exit 0, expected a failure"
  elif [ -e "$out" ]; then
    fail "$description" "exit $code, but an output file was left behind"
  elif compgen -G "$out.*" > /dev/null; then
    fail "$description" "exit $code, but a temporary file was left behind"
  else
    pass "$description"
  fi
}

# expect_message <description> <text>: the last run's output names the reason (checked after expect_failure).
expect_message() {
  if grep -Fq "$2" <<< "$log"; then
    pass "$1: says \"$2\""
  else
    fail "$1: says \"$2\"" "the message is missing"
  fi
}

uris='.runs[].results[] | [.ruleId, (.locations | map(.physicalLocation.artifactLocation.uri))]'

# The script's list is exactly EXPECTED_RULES: the cases below catch a rule missing from it, this catches an extra one.
script_rules=$(sed -n '/^readonly TEST_ONLY_RULES=(/,/^)/{/^  cs\//p}' "$script" | tr -d ' ' | sort)
if [ "$script_rules" = "$(printf '%s\n' "${EXPECTED_RULES[@]}" | sort)" ]; then
  pass "rule_list_matches"
else
  log=$script_rules
  fail "rule_list_matches" "the script lists other rules than EXPECTED_RULES"
fi

# listed_rule_under_tests_removed: each listed rule, with every location under tests/, is removed; the log counts it.
for rule in "${EXPECTED_RULES[@]}"; do
  fresh
  sarif "$(run_of "$(result "$rule" tests/BrutalEngine.Core.Tests/A.cs)" "$(result cs/sql-injection tests/A.cs)")" > "$in"
  expect_results "listed_rule_under_tests_removed: $rule" "$uris" <<< '["cs/sql-injection",["tests/A.cs"]]'
  if ! grep -Eq "^removed 1 $rule\$" <<< "$log"; then
    fail "listed_rule_under_tests_removed: $rule is counted" "no 'removed 1 $rule' line"
  fi
done

fresh
sarif "$(run_of "$(result cs/call-to-gc tests/A.cs tests/B/C.cs)")" > "$in"
expect_results "listed_rule_under_tests_removed: several locations, all under tests/" "[$uris]" <<< '[]'

# Related locations are context, not where the result is: cs/useless-upcast relates its test-code results to the
# type's declaration in src/ and to a reference assembly, so they don't keep a result.
fresh
sarif "$(run_of "$(related "$(result cs/useless-upcast tests/A.cs)" src/BrutalEngine.Graphics/Viewport.cs \
  file:///home/runner/work/_temp/System.Runtime.dll)" "$(result cs/sql-injection src/A.cs)")" > "$in"
expect_results "listed_rule_under_tests_removed: related locations outside tests/" "$uris" \
  <<< '["cs/sql-injection",["src/A.cs"]]'

# unlisted_rule_under_tests_kept: rules not on the list keep their results under tests/, including ids that only start
# or end like a listed one, and a result with no rule id.
fresh
sarif "$(run_of \
  "$(result cs/comparison-of-identical-expressions tests/A.cs)" \
  "$(result cs/sql-injection tests/A.cs)" \
  "$(result cs/linq/missed-where tests/A.cs)" \
  "$(result cs/call-to-gc-extra tests/A.cs)" \
  "$(result x/cs/call-to-gc tests/A.cs)" \
  "$(result CS/CALL-TO-GC tests/A.cs)" \
  "$(jq -c 'del(.ruleId, .rule)' <<< "$(result cs/call-to-gc tests/A.cs)")")" > "$in"
expect_results "unlisted_rule_under_tests_kept" "$uris" <<'EOF'
["cs/comparison-of-identical-expressions",["tests/A.cs"]]
["cs/sql-injection",["tests/A.cs"]]
["cs/linq/missed-where",["tests/A.cs"]]
["cs/call-to-gc-extra",["tests/A.cs"]]
["x/cs/call-to-gc",["tests/A.cs"]]
["CS/CALL-TO-GC",["tests/A.cs"]]
[null,["tests/A.cs"]]
EOF

# The rule id is read from rule.id when ruleId is absent (SARIF allows either).
fresh
sarif "$(run_of "$(jq -c 'del(.ruleId)' <<< "$(result cs/call-to-gc tests/A.cs)")" "$(result cs/sql-injection src/A.cs)")" > "$in"
expect_results "listed_rule_under_tests_removed: rule id from rule.id" "$uris" <<< '["cs/sql-injection",["src/A.cs"]]'

# listed_rule_under_src_kept: a listed rule outside tests/ is kept, including paths that only look like tests/ and
# results with no location to judge by.
fresh
sarif "$(run_of \
  "$(result cs/call-to-gc src/BrutalEngine.Core/A.cs)" \
  "$(result cs/call-to-gc samples/HelloWindow/A.cs)" \
  "$(result cs/call-to-gc src/tests/A.cs)" \
  "$(result cs/call-to-gc testsuite/A.cs)" \
  "$(result cs/call-to-gc Tests/A.cs)" \
  "$(result cs/call-to-gc /tests/A.cs)" \
  "$(result cs/call-to-gc file:///home/runner/work/tests/A.cs)" \
  "$(result cs/call-to-gc)" \
  "$(jq -c '.locations[0].physicalLocation |= del(.artifactLocation)' <<< "$(result cs/call-to-gc tests/A.cs)")")" > "$in"
expect_results "listed_rule_under_src_kept" "$uris" <<'EOF'
["cs/call-to-gc",["src/BrutalEngine.Core/A.cs"]]
["cs/call-to-gc",["samples/HelloWindow/A.cs"]]
["cs/call-to-gc",["src/tests/A.cs"]]
["cs/call-to-gc",["testsuite/A.cs"]]
["cs/call-to-gc",["Tests/A.cs"]]
["cs/call-to-gc",["/tests/A.cs"]]
["cs/call-to-gc",["file:///home/runner/work/tests/A.cs"]]
["cs/call-to-gc",[]]
["cs/call-to-gc",[null]]
EOF

# listed_rule_mixed_locations_kept: one location outside tests/, first, last or in the middle, keeps the result.
fresh
sarif "$(run_of \
  "$(result cs/dispose-not-called-on-throw tests/A.cs src/B.cs)" \
  "$(result cs/dispose-not-called-on-throw src/B.cs tests/A.cs)" \
  "$(related "$(result cs/dispose-not-called-on-throw tests/A.cs samples/D.cs tests/C.cs)" tests/E.cs)")" > "$in"
expect_results "listed_rule_mixed_locations_kept" "$uris" <<'EOF'
["cs/dispose-not-called-on-throw",["tests/A.cs","src/B.cs"]]
["cs/dispose-not-called-on-throw",["src/B.cs","tests/A.cs"]]
["cs/dispose-not-called-on-throw",["tests/A.cs","samples/D.cs","tests/C.cs"]]
EOF

# other_results_byte_identical (the issue's name): everything but the removed results is identical as JSON values, with
# key order and number literals preserved (the output is compact JSON, so whitespace isn't kept): the log's top-level
# fields, the run metadata (tool and its rules, invocations, artifacts, a number too big for a double) and every kept
# result, in order.
fresh
kept1=$(related "$(result cs/sql-injection src/A.cs tests/B.cs)" src/C.cs)
kept2=$(result cs/call-to-gc src/A.cs)
kept3=$(result cs/path-combine tests/A.cs src/B.cs)
sarif "$(run_of "$kept1" "$(result cs/call-to-gc tests/A.cs)" "$kept2" "$(result cs/path-combine tests/B.cs)" "$kept3")" \
  > "$in"
expected=$(sarif "$(run_of "$kept1" "$kept2" "$kept3")")
filter
if [ "$code" -ne 0 ]; then
  fail "other_results_byte_identical" "exit $code, expected 0"
elif [ "$(jq -c . "$out")" != "$expected" ]; then
  fail "other_results_byte_identical" "output differs from the input without the two removed results"
elif [ "$(jq -c 'del(.runs)' "$out")" != "$(jq -c 'del(.runs)' "$in")" ] \
  || [ "$(jq -c '.runs[] | del(.results)' "$out")" != "$(jq -c '.runs[] | del(.results)' "$in")" ] \
  || ! grep -q '12345678901234567890123' "$out"; then
  fail "other_results_byte_identical" "the log or run metadata changed"
else
  pass "other_results_byte_identical"
fi

# Nothing to remove: the output holds the same JSON as the input.
fresh
sarif "$(run_of "$kept1" "$kept2")" > "$in"
filter
if [ "$code" -ne 0 ] || [ "$(jq -c . "$out")" != "$(jq -c . "$in")" ]; then
  fail "other_results_byte_identical: nothing removed" "exit $code, or the output differs from the input"
else
  pass "other_results_byte_identical: nothing removed"
fi

# multiple_runs_each_filtered: each run is filtered on its own; a run with an empty results array stays as it is.
fresh
sarif \
  "$(run_of "$(result cs/call-to-gc tests/A.cs)" "$(result cs/sql-injection tests/A.cs)")" \
  "$(run_of "$(result cs/path-combine src/A.cs)" "$(result cs/path-combine tests/A.cs)")" \
  "$(run_of "$(result cs/call-to-gc tests/A.cs)")" \
  "$(run_of)" > "$in"
expect_results "multiple_runs_each_filtered" '[.runs[] | .results | map(.ruleId)]' \
  <<< '[["cs/sql-injection"],["cs/path-combine"],[],[]]'
if ! grep -Eq '^removed 2 cs/call-to-gc$' <<< "$log" || ! grep -Eq '^removed 1 cs/path-combine$' <<< "$log"; then
  fail "multiple_runs_each_filtered: counted across runs" "expected 'removed 2 cs/call-to-gc' and 'removed 1 cs/path-combine'"
fi

# malformed_sarif_fails: anything that isn't one SARIF log with a non-empty runs array of runs, each with a results
# array (empty is fine), fails, and leaves no output file to upload. An upload with no runs, or a run without results,
# would close every open alert of the category.
fresh
printf '{"version": "2.1.0", "runs": [' > "$in"
expect_failure "malformed_sarif_fails: truncated JSON"
fresh
: > "$in"
expect_failure "malformed_sarif_fails: empty file"
fresh
printf '%s\n%s\n' "$(sarif "$(run_of)")" "$(sarif "$(run_of)")" > "$in"
expect_failure "malformed_sarif_fails: two JSON documents"
fresh
printf '[]' > "$in"
expect_failure "malformed_sarif_fails: not an object"
fresh
printf '{"version": "2.1.0"}' > "$in"
expect_failure "malformed_sarif_fails: no runs"
fresh
printf '{"version": "2.1.0", "runs": {}}' > "$in"
expect_failure "malformed_sarif_fails: runs not an array"
fresh
sarif > "$in"
expect_failure "empty_runs_fails"
expect_message "empty_runs_fails" "the SARIF log has no runs"
fresh
sarif "$(run_of "$(result cs/sql-injection src/A.cs)")" "$(jq -c 'del(.results)' <<< "$(run_of)")" > "$in"
expect_failure "run_without_results_fails"
expect_message "run_without_results_fails" "a run has no results array"
fresh
sarif "$(jq -c '.results = null' <<< "$(run_of)")" > "$in"
expect_failure "run_without_results_fails: results null"
expect_message "run_without_results_fails: results null" "a run has no results array"
fresh
printf '{"version": "2.1.0", "runs": [[]]}' > "$in"
expect_failure "malformed_sarif_fails: run not an object"
fresh
printf '{"version": "2.1.0", "runs": [{"results": {}}]}' > "$in"
expect_failure "malformed_sarif_fails: results not an array"
fresh
printf '{"version": "2.1.0", "runs": [{"results": [3]}]}' > "$in"
expect_failure "malformed_sarif_fails: result not an object (jq error)"
fresh
printf '{"version": "2.1.0", "runs": [{"results": [{"ruleId": "cs/call-to-gc", "locations": 7}]}]}' > "$in"
expect_failure "malformed_sarif_fails: locations not an array (jq error)"

# missing_file_fails: a missing input, or missing arguments, fails without writing an output file.
fresh
expect_failure "missing_file_fails"
fresh
sarif "$(run_of)" > "$in"
set +e
log=$(bash "$script" "$in" 2>&1)
code=$?
set -e
if [ "$code" -eq 0 ]; then
  fail "missing_file_fails: no output argument" "exit 0, expected a failure"
else
  pass "missing_file_fails: no output argument"
fi
fresh
sarif "$(run_of)" > "$in"
out="$work/no-such-directory/out.sarif"
filter
if [ "$code" -eq 0 ] || [ -e "$out" ]; then
  fail "missing_file_fails: output directory missing" "exit $code, expected a failure and no output"
else
  pass "missing_file_fails: output directory missing"
fi

if [ "$failures" -ne 0 ]; then
  echo "$failures SARIF filter check(s) failed."
  exit 1
fi
echo "All SARIF filter checks passed."
