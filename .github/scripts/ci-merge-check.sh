#!/usr/bin/env bash
# Decides how much of the full tier a push to main still needs, from what the merged PR's CI already proved.
#
# A squash commit whose tree is identical to the merged PR's head tree means the head contained main at merge time.
# The PR's run tested the head merged with main as main was when the run started, and global.json plus the lock files
# pin the toolchain and packages. So that run built and tested exactly this tree, unless main gained a change after
# the run started and then reverted it before the merge: the trees still match, but the run tested the head plus that
# change. The nightly full run, which never uses this check, is the backstop for that gap and for runner-image drift.
#
# Prints key=value lines on stdout (diagnostics go to stderr):
#   decision=none         the event isn't a push (pull requests, the nightly schedule and manual runs keep their tiers)
#   decision=verified     same tree, and the PR passed the full tier: nothing needs to run again
#   decision=smoke-delta  same tree, and the PR passed the fast tier: only the full tier's extra smoke OSes need to run
#   decision=full         anything else: the tree differs, no single merged PR, the PR's CI didn't pass, an API error,
#                         malformed JSON, or any other doubt
#   reason=<text>         why, for the log and the job summary
#   pr=<number>, pr_url=<url>, proof_url=<url>  the merged PR and its passing "CI passed" job (verified and smoke-delta)
#
# Inputs (environment):
#   EVENT_NAME     github.event_name
#   REF            github.ref; only refs/heads/main qualifies
#   REPO           owner/name
#   SHA            the pushed commit
#   SMOKE_FAST_OS  the smoke matrix OSes of the fast tier (space-separated runner labels)
#   SMOKE_FULL_OS  the OSes the full tier adds to the smoke matrix
#   CI_MERGE_GH    the gh executable (default gh). The self-test points it at recorded API answers.
#
# The job names below must match ci.yml (the ci-ok job and the vulkan-smoke job's name template). A rename that
# misses this file can only cost time: a missing check run never matches, so the push falls back to the full tier.
# The tiers are described in docs/06-ci-cd-and-release.md ("CI tiers"). Keep the two in sync.
set -euo pipefail

readonly CI_PASSED_JOB='CI passed'
readonly SMOKE_JOB_PREFIX='Vulkan smoke, JIT + NativeAOT'

gh_bin=${CI_MERGE_GH:-gh}

decide() {
  local decision=$1 reason=$2
  echo "decision=$decision"
  echo "reason=$reason"
  exit 0
}

# jq with carriage returns removed: jq.exe on Windows writes CRLF, which would break every comparison locally.
json() {
  jq "$@" | tr -d '\r'
}

if [ "${EVENT_NAME:-}" != "push" ]; then
  decide none "event '${EVENT_NAME:-}' is not a push, so the merge check doesn't apply"
fi
if [ "${REF:-}" != "refs/heads/main" ]; then
  decide full "push to '${REF:-}', not refs/heads/main"
fi
sha_pattern='^[0-9a-f]{40}$'
if ! [[ ${SHA:-} =~ $sha_pattern ]]; then
  decide full "the pushed commit '${SHA:-}' is not a full commit SHA"
fi
if ! [[ ${REPO:-} =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; then
  decide full "the repository name '${REPO:-}' is not owner/name"
fi
read -r -a fast_os <<< "${SMOKE_FAST_OS:-}"
read -r -a full_os <<< "${SMOKE_FULL_OS:-}"
if [ "${#fast_os[@]}" -eq 0 ] || [ "${#full_os[@]}" -eq 0 ]; then
  decide full "SMOKE_FAST_OS and SMOKE_FULL_OS must both name at least one OS"
fi

# 1. The PR merged as this commit. The endpoint also lists open PRs that merely contain the commit, so keep only
# PRs merged into main whose merge commit is this one, and require exactly one.
if ! pulls=$("$gh_bin" api "repos/$REPO/commits/$SHA/pulls"); then
  decide full "API error listing the PRs for $SHA"
fi
# Fields are joined with the unit separator, which can't appear in them and, unlike a tab, keeps empty fields.
readonly US=$'\x1f'
if ! merged=$(printf '%s' "$pulls" | json -r --arg sha "$SHA" \
  '[.[] | select(.merged_at != null and .merge_commit_sha == $sha and .base.ref == "main")]
   | [length, .[0].number, .[0].head.sha, .[0].html_url] | map(if . == null then "" else tostring end) | join("\u001f")'); then
  decide full "malformed JSON listing the PRs for $SHA"
fi
IFS=$US read -r count pr head pr_url <<< "$merged"
if [ "$count" != 1 ]; then
  decide full "found $count PRs merged into main as $SHA, not exactly one"
fi
if ! [[ $pr =~ ^[0-9]+$ ]] || ! [[ $head =~ $sha_pattern ]]; then
  decide full "the merged PR's number or head SHA is malformed"
fi
if [ "$head" = "$SHA" ]; then
  decide full "PR #$pr's head is the pushed commit itself, so its check runs include this run"
fi

# 2. Same tree: the squash commit's tree must equal the tree the PR head's CI tested.
tree_of() {
  local commit=$1 body tree
  body=$("$gh_bin" api "repos/$REPO/commits/$commit") || return 1
  tree=$(printf '%s' "$body" | json -r '.commit.tree.sha // ""') || return 1
  [[ $tree =~ $sha_pattern ]] || return 1
  echo "$tree"
}
if ! tree=$(tree_of "$SHA"); then
  decide full "could not read the tree of $SHA"
fi
if ! head_tree=$(tree_of "$head"); then
  decide full "could not read the tree of PR #$pr's head $head"
fi
if [ "$tree" != "$head_tree" ]; then
  decide full "tree $tree differs from PR #$pr's head tree $head_tree"
fi

# 3. What the PR's CI proved at that head. Only GitHub Actions check runs count. filter=all lists every attempt of
# every workflow run on the head, not just the latest attempt, and every check run with a given name must have
# succeeded: a job that failed and then passed on a re-run, or a cancelled duplicate run, means more testing.
# One line per job name: "true" or "false" (all succeeded), the name, and the first run's link.
if ! checks=$("$gh_bin" api --paginate "repos/$REPO/commits/$head/check-runs?per_page=100&filter=all"); then
  decide full "API error listing PR #$pr's check runs"
fi
if ! jobs=$(printf '%s' "$checks" | json -r -s \
  '[.[].check_runs[] | select(.app.slug == "github-actions")] | group_by(.name)[]
   | [all(.conclusion == "success"), .[0].name, .[0].html_url] | map(if . == null then "" else tostring end) | join("\u001f")'); then
  decide full "malformed JSON listing PR #$pr's check runs"
fi
# passed <job name>: succeeds if the job ran and every run of it succeeded. Sets job_url to its first run's link.
passed() {
  local ok name url
  while IFS=$US read -r ok name url; do
    if [ "$name" = "$1" ]; then
      job_url=$url
      [ "$ok" = true ]
      return
    fi
  done <<< "$jobs"
  return 1
}
if ! passed "$CI_PASSED_JOB"; then
  decide full "PR #$pr's '$CI_PASSED_JOB' did not succeed at $head"
fi
proof_url=$job_url
url_pattern='^https://github\.com/[A-Za-z0-9_./-]+$'
if ! [[ $pr_url =~ $url_pattern ]] || ! [[ $proof_url =~ $url_pattern ]]; then
  decide full "PR #$pr's links are malformed"
fi

smoke_passed_on() {
  local os
  for os in "$@"; do
    passed "$SMOKE_JOB_PREFIX ($os)" || return 1
  done
}
echo "pr=$pr"
echo "pr_url=$pr_url"
echo "proof_url=$proof_url"
if smoke_passed_on "${fast_os[@]}" "${full_os[@]}"; then
  decide verified "same tree as PR #$pr's head $head, which passed the full tier"
fi
# The fast tier always runs the fast smoke OSes; the docs tier runs none, so a docs-tier PR doesn't qualify.
if smoke_passed_on "${fast_os[@]}"; then
  decide smoke-delta "same tree as PR #$pr's head $head, which passed the fast tier"
fi
decide full "PR #$pr passed neither the fast nor the full tier's smoke at $head"
