#!/usr/bin/env bash
# Self-test for ci-merge-check.sh. CI runs it before the merge check is used, so a broken rule fails loudly instead of
# silently skipping jobs on main. No network: a fake gh serves API answers shaped like the real ones (recorded from
# PR #38, merged as f2a8d86) out of a temporary directory, and logs every call.
set -euo pipefail

if ! command -v jq > /dev/null; then
  echo "FAIL  jq is required (it is preinstalled on GitHub's runners)"
  exit 1
fi

script="$(cd "$(dirname "$0")" && pwd)/ci-merge-check.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failures=0

readonly REPO_NAME=harry-stanley/brutal-engine
readonly SQUASH=f2a8d861e6bf7c05d867ba141b86bd4fed85e20c
readonly HEAD=8ef2b1e7f381143131275ba1d9ed0e9149a346e5
readonly TREE=c5d0742a12a98cdaddfd232d173ec915dc62285b
readonly OTHER_TREE=0123456789abcdef0123456789abcdef01234567
readonly OTHER_SHA=89abcdef0123456789abcdef0123456789abcdef
readonly RUN_URL=https://github.com/harry-stanley/brutal-engine/actions/runs/45596003295/job
# filter=all lists every attempt of every workflow run, so an earlier failed attempt is visible.
readonly CHECKS_QUERY='check-runs?per_page=100&filter=all'

# The fake gh: `gh api [--paginate] <path>` prints the fixture file named after the path (page 1), or fails like a
# 404. With --paginate it then prints the fixture's further pages (<file>.page2, <file>.page3, ...) one after another,
# as gh does when it follows the Link header: each page is its own JSON object. A check-runs list can span pages, and
# a caller that reads only page 1 judges the PR on part of its jobs, so the fake rejects a check-runs call without
# --paginate outright.
cat > "$work/gh" << 'EOF'
#!/usr/bin/env bash
set -euo pipefail
[ "$1" = api ] || exit 2
shift
paginate=false
if [ "$1" = --paginate ]; then
  paginate=true
  shift
fi
echo "$1" >> "$FAKE_GH_DIR/calls"
if [[ $1 == */check-runs* ]] && ! $paginate; then
  echo "gh (fake): a check-runs request without --paginate reads only the first page" >&2
  exit 3
fi
file="$FAKE_GH_DIR/$(printf '%s' "$1" | tr '/?=&' '____')"
if [ ! -f "$file" ]; then
  echo "gh: Not Found (HTTP 404)" >&2
  exit 1
fi
cat "$file"
if $paginate; then
  page=2
  while [ -f "$file.page$page" ]; do
    cat "$file.page$page"
    page=$((page + 1))
  done
fi
EOF
chmod +x "$work/gh"

# Starts a case with no fixtures, so each case gets only the API answers it sets up. pushed is the commit the PR list
# is served for; checks_on is the commit the check runs are served for; full_os is the merge check's SMOKE_FULL_OS
# input (ci.yml's value unless a case overrides it).
fresh() {
  fixtures="$work/case$((++case_number))"
  mkdir -p "$fixtures"
  : > "$fixtures/calls"
  pushed=$SQUASH
  checks_on=$HEAD
  full_os=windows-2025
}
case_number=0

# fixture <API path> [page]: stores stdin as the answer for that path (page 1 unless a later page is given).
fixture() {
  local path=$1 page=${2-1} suffix=""
  [ "$page" -eq 1 ] || suffix=".page$page"
  cat > "$fixtures/$(printf '%s' "$path" | tr '/?=&' '____')$suffix"
}

pr_json() {
  local number=$1 merge_sha=$2 head=$3 merged_at=${4-\"2026-11-25T10:35:02Z\"} base=${5-main}
  printf '{"number":%s,"html_url":"https://github.com/%s/pull/%s","state":"closed","merged_at":%s,' \
    "$number" "$REPO_NAME" "$number" "$merged_at"
  printf '"merge_commit_sha":"%s","head":{"sha":"%s","ref":"feat/x"},"base":{"sha":"%s","ref":"%s"}}' \
    "$merge_sha" "$head" "$OTHER_SHA" "$base"
}

pulls() {
  local IFS=,
  printf '[%s]' "$*" | fixture "repos/$REPO_NAME/commits/$pushed/pulls"
}

commit() {
  local sha=$1 tree=$2
  printf '{"sha":"%s","commit":{"message":"m","tree":{"sha":"%s"}},"files":[]}' "$sha" "$tree" \
    | fixture "repos/$REPO_NAME/commits/$sha"
}

# check_runs "<name>=<conclusion>[@<app slug>]" ...; conclusion "null" means still running. Serves page 1 of the list;
# check_runs_on_page <page> ... serves a later one (ids stay unique across pages). The script doesn't read total_count.
check_runs() {
  check_runs_on_page 1 "$@"
}
check_runs_on_page() {
  local page=$1 entries=() run name conclusion app id
  shift
  id=$((139142244000 + (page - 1) * 1000))
  for run in "$@"; do
    name=${run%%=*}
    conclusion=${run#*=}
    app=github-actions
    if [[ $conclusion == *@* ]]; then
      app=${conclusion#*@}
      conclusion=${conclusion%%@*}
    fi
    [ "$conclusion" = null ] || conclusion="\"$conclusion\""
    id=$((id + 1))
    entries+=("$(printf '{"id":%s,"name":"%s","head_sha":"%s","status":"completed","conclusion":%s,"html_url":"%s/%s","app":{"slug":"%s"}}' \
      "$id" "$name" "$checks_on" "$conclusion" "$RUN_URL" "$id" "$app")")
  done
  local IFS=,
  printf '{"total_count":%s,"check_runs":[%s]}' "${#entries[@]}" "${entries[*]}" \
    | fixture "repos/$REPO_NAME/commits/$checks_on/$CHECKS_QUERY" "$page"
}

readonly FAST_RUNS=("CI passed=success" "Select CI tier=success" "Format=success"
  "Build and test (windows-2025)=success" "Build and test (ubuntu-24.04)=success"
  "Vulkan smoke, JIT + NativeAOT (ubuntu-24.04)=success" "CodeQL=success@github-advanced-security")
readonly FULL_RUNS=("${FAST_RUNS[@]}"
  "Vulkan smoke, JIT + NativeAOT (windows-2025)=success")

# The PR #38 shape: merged as SQUASH, head HEAD, both with tree TREE.
same_tree_pr() {
  pulls "$(pr_json 38 "$SQUASH" "$HEAD")"
  commit "$SQUASH" "$TREE"
  commit "$HEAD" "$TREE"
}

run_check() {
  local event=$1 ref=${2-refs/heads/main} sha=${3-$SQUASH}
  EVENT_NAME=$event REF=$ref REPO=$REPO_NAME SHA=$sha SMOKE_FAST_OS="ubuntu-24.04" \
    SMOKE_FULL_OS="$full_os" CI_MERGE_GH="$work/gh" FAKE_GH_DIR="$fixtures" bash "$script" 2> /dev/null
}

# check <expected decision> <description> [event] [ref] [sha]
check() {
  local expected=$1 description=$2 output status=0 actual
  output=$(run_check "${3-push}" "${4-refs/heads/main}" "${5-$SQUASH}") || status=$?
  actual=$(printf '%s\n' "$output" | sed -n 's/^decision=//p')
  if [ "$status" -ne 0 ]; then
    echo "FAIL  expected $expected, but the script exited $status: $description"
    failures=$((failures + 1))
  elif [ "$actual" != "$expected" ]; then
    echo "FAIL  expected $expected, got ${actual:-nothing}: $description"
    failures=$((failures + 1))
  elif [ "$expected" = verified ] || [ "$expected" = smoke-delta ]; then
    # Skipping work needs the evidence linked: the PR and its passing "CI passed" job.
    if ! grep -qx "pr=38" <<< "$output" || ! grep -qx "pr_url=https://github.com/$REPO_NAME/pull/38" <<< "$output" \
      || ! grep -qx "proof_url=$RUN_URL/139142244001" <<< "$output"; then
      echo "FAIL  $expected without the PR and proof links: $description"
      failures=$((failures + 1))
    else
      echo "ok    $expected  $description"
    fi
  elif [ "$expected" = none ] && [ -s "$fixtures/calls" ]; then
    echo "FAIL  none, but the GitHub API was called: $description"
    failures=$((failures + 1))
  else
    echo "ok    $expected  $description"
  fi
}

# Verified: same tree, and the PR passed the full tier.
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}"
check verified "same tree + full tier passed"
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}" "CI passed=success" "Vulkan smoke, JIT + NativeAOT (windows-2025)=success"
check verified "same tree + full tier passed in two workflow runs or attempts on the head"
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}" "CodeQL=failure@github-advanced-security"
check verified "same tree + full tier; another app's failed check doesn't count"
fresh
pulls "$(pr_json 37 "$OTHER_SHA" "$OTHER_SHA" null)" "$(pr_json 38 "$SQUASH" "$HEAD")"
commit "$SQUASH" "$TREE"; commit "$HEAD" "$TREE"; check_runs "${FULL_RUNS[@]}"
check verified "same tree + full tier; an open PR that contains the commit is ignored"

# Smoke delta: same tree, and the PR passed the fast tier.
fresh; same_tree_pr; check_runs "${FAST_RUNS[@]}"
check smoke-delta "same tree + fast tier passed"
fresh; same_tree_pr; check_runs "${FAST_RUNS[@]}" "Vulkan smoke, JIT + NativeAOT (ubuntu-24.04-arm)=success"
check smoke-delta "same tree + fast tier plus a smoke run on an OS the full tier doesn't add"
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}" "Vulkan smoke, JIT + NativeAOT (windows-2025)=failure"
check smoke-delta "same tree + a full-tier smoke run failed on the head"
# Every full-tier OS must pass, not only the last one checked: with two full-tier OSes, the first one failed.
fresh; full_os="windows-2025 ubuntu-24.04-arm"; same_tree_pr
check_runs "${FAST_RUNS[@]}" "Vulkan smoke, JIT + NativeAOT (windows-2025)=failure" \
  "Vulkan smoke, JIT + NativeAOT (ubuntu-24.04-arm)=success"
check smoke-delta "same tree + two full-tier OSes, and the first one's smoke failed"

# Check runs on more than one page: the script must read every page (gh api --paginate).
fresh; same_tree_pr; check_runs "${FAST_RUNS[@]}"
check_runs_on_page 2 "Vulkan smoke, JIT + NativeAOT (windows-2025)=success"
check verified "same tree + full tier, with the full tier's smoke run on page 2 of the check runs"
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}"
check_runs_on_page 2 "Build and test (ubuntu-24.04)=success" "Vulkan smoke, JIT + NativeAOT (ubuntu-24.04)=failure"
check full "same tree + full tier, but a failed fast-tier smoke run on page 2 of the check runs"

# The fake itself: it serves every page with --paginate, and refuses a check-runs call without it.
check_fake() {
  local expected=$1 description=$2 output status=0
  shift 2
  output=$(FAKE_GH_DIR="$fixtures" "$work/gh" api "$@" 2> /dev/null) || status=$?
  if [ "$expected" = refused ] && [ "$status" -ne 0 ] && [ -z "$output" ]; then
    echo "ok    refused  $description"
  elif [ "$expected" = "two pages" ] && [ "$status" -eq 0 ] \
    && [ "$(printf '%s' "$output" | jq -s 'length' | tr -d '\r')" = 2 ]; then
    echo "ok    two pages  $description"
  else
    echo "FAIL  fake gh: expected $expected, got exit $status and $(printf '%s' "$output" | wc -c) bytes: $description"
    failures=$((failures + 1))
  fi
}
fresh; check_runs "${FAST_RUNS[@]}"; check_runs_on_page 2 "CI passed=success"
check_fake refused "fake gh: check-runs call without --paginate" "repos/$REPO_NAME/commits/$HEAD/$CHECKS_QUERY"
check_fake "two pages" "fake gh: check-runs call with --paginate" --paginate "repos/$REPO_NAME/commits/$HEAD/$CHECKS_QUERY"

# Full: the tree differs.
fresh; pulls "$(pr_json 38 "$SQUASH" "$HEAD")"; commit "$SQUASH" "$TREE"; commit "$HEAD" "$OTHER_TREE"
check_runs "${FULL_RUNS[@]}"
check full "tree differs from the PR head's tree"

# Full: no single PR merged as this commit.
fresh; pulls; commit "$SQUASH" "$TREE"
check full "no PR for the commit"
fresh; pulls "$(pr_json 38 "$SQUASH" "$HEAD" null)"; commit "$SQUASH" "$TREE"; commit "$HEAD" "$TREE"
check_runs "${FULL_RUNS[@]}"
check full "PR not merged"
fresh; pulls "$(pr_json 38 "$OTHER_SHA" "$HEAD")"; commit "$SQUASH" "$TREE"; commit "$HEAD" "$TREE"
check_runs "${FULL_RUNS[@]}"
check full "PR merged as a different commit"
fresh; pulls "$(pr_json 38 "$SQUASH" "$HEAD" '"2026-11-25T10:35:02Z"' release)"; commit "$SQUASH" "$TREE"
commit "$HEAD" "$TREE"; check_runs "${FULL_RUNS[@]}"
check full "PR merged into another branch"
fresh; pulls "$(pr_json 38 "$SQUASH" "$HEAD")" "$(pr_json 39 "$SQUASH" "$HEAD")"; commit "$SQUASH" "$TREE"
commit "$HEAD" "$TREE"; check_runs "${FULL_RUNS[@]}"
check full "two PRs claim the merge commit"
# Everything else would verify, so only the guard against the PR head being the pushed commit makes this full.
fresh; pulls "$(pr_json 38 "$SQUASH" "$SQUASH")"; commit "$SQUASH" "$TREE"; checks_on=$SQUASH
check_runs "${FULL_RUNS[@]}"
check full "PR head is the pushed commit itself"

# Full: the PR's CI didn't prove the tree.
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]/CI passed=success/CI passed=failure}"
check full "CI failed (CI passed concluded failure)"
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}" "CI passed=failure"
check full "CI passed failed in another workflow run on the head"
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}" "Build and test (windows-2025)=failure" "CI passed=failure"
check full "a job failed in an earlier attempt, then passed on a re-run"
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}" "Select CI tier=cancelled" "CI passed=cancelled"
check full "a cancelled duplicate run on the head"
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]/CI passed=success/CI passed=null}"
check full "CI still running (no conclusion)"
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]/CI passed=success/Select CI tier=success}"
check full "no CI passed check run"
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]/CI passed=success/CI passed=success@some-other-app}"
check full "CI passed from an app other than GitHub Actions"
fresh; same_tree_pr; check_runs "CI passed=success" "Select CI tier=success" "Vulkan smoke, JIT + NativeAOT (\${{ matrix.os }})=skipped"
check full "docs-tier PR (nothing built or smoke-tested)"
fresh; same_tree_pr
check_runs "${FAST_RUNS[@]/Vulkan smoke, JIT + NativeAOT (ubuntu-24.04)=success/Vulkan smoke, JIT + NativeAOT (ubuntu-24.04)=skipped}" \
  "Vulkan smoke, JIT + NativeAOT (windows-2025)=success"
check full "fast tier's smoke OS skipped"

# Full: API errors (a missing fixture fails like an HTTP error).
fresh
check full "API error listing the PRs"
fresh; pulls "$(pr_json 38 "$SQUASH" "$HEAD")"; commit "$HEAD" "$TREE"; check_runs "${FULL_RUNS[@]}"
check full "API error reading the pushed commit"
fresh; pulls "$(pr_json 38 "$SQUASH" "$HEAD")"; commit "$SQUASH" "$TREE"; check_runs "${FULL_RUNS[@]}"
check full "API error reading the PR head commit"
fresh; same_tree_pr
check full "API error listing the check runs"

# Full: malformed JSON.
fresh; printf '[{"number":38,' | fixture "repos/$REPO_NAME/commits/$SQUASH/pulls"
check full "malformed JSON: truncated PR list"
fresh; printf '{"message":"Bad credentials"}' | fixture "repos/$REPO_NAME/commits/$SQUASH/pulls"
check full "malformed JSON: an error object instead of the PR list"
fresh; same_tree_pr; printf '{"sha":"%s","commit":{"tree":{}}}' "$HEAD" | fixture "repos/$REPO_NAME/commits/$HEAD"
check_runs "${FULL_RUNS[@]}"
check full "malformed JSON: commit without a tree SHA"
fresh; same_tree_pr; printf '{"total_count":1,"check_runs":' | fixture "repos/$REPO_NAME/commits/$HEAD/$CHECKS_QUERY"
check full "malformed JSON: truncated check runs"
fresh; same_tree_pr; printf '{"total_count":0}' | fixture "repos/$REPO_NAME/commits/$HEAD/$CHECKS_QUERY"
check full "malformed JSON: check runs without a list"

# Full: bad inputs.
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}"
check full "push to a branch other than main" push refs/heads/feat/x
# Fixtures keyed by the abbreviated SHA would verify, so only the SHA format check makes this full.
fresh; pushed=f2a8d86; pulls "$(pr_json 38 f2a8d86 "$HEAD")"; commit f2a8d86 "$TREE"; commit "$HEAD" "$TREE"
check_runs "${FULL_RUNS[@]}"
check full "pushed SHA is abbreviated" push refs/heads/main f2a8d86

# Not applied: other events keep their own tiers and never call the API.
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}"
check none "pull_request event" pull_request refs/pull/38/merge
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}"
check none "schedule event (nightly)" schedule
fresh; same_tree_pr; check_runs "${FULL_RUNS[@]}"
check none "workflow_dispatch event (manual run)" workflow_dispatch

if [ "$failures" -ne 0 ]; then
  echo "$failures merge check(s) failed."
  exit 1
fi
echo "All merge checks passed."
