#!/usr/bin/env bash
# Merges an agent's pull request into main when, and only when, every condition below holds. This is the one
# path by which code reaches main unattended, so it fails closed: anything missing or unclear is "not eligible",
# and any GitHub read that fails is an error, never a merge.
#
#   bash scripts/agent/merge-gate.sh <pr-number>
#
# Conditions, each printed as its reason line on stdout when unmet (all unmet ones, in this order):
#   ci-ok is pending                     no completed `ci-ok` check run (by GitHub Actions) on the head commit
#   ci-ok failed                         a completed `ci-ok` run on the head commit concluded other than success
#   verdict is not approve               verdict.sh does not give approve
#   verdict is not for the head commit   the verdict's sha is not the PR's head sha
#   risk is not low                      the slice's `## Risk` section does not start with the word `low`
#   needs-owner label is set             the PR carries the needs-owner label
# The runner reads these lines: it waits on `ci-ok is pending` alone, asks the owner on `risk is not low`
# alone, and escalates otherwise, so they are an interface.
#
# The slice is the PR's first closing issue reference; without one, risk is not low. Risk is low when the first
# non-empty line of the slice body's first and only `## Risk` section (up to the next `## ` heading) has `low`
# as its first word, case-sensitive, allowing one trailing comma or period: `low, a copy change` passes,
# `Low`, `low-ish`, an empty or missing section, or two Risk sections do not.
# When eligible it runs `gh pr merge <pr> --merge --match-head-commit <head sha>`, so a commit pushed after the
# checks cannot slip in, and then `board.sh status <slice> Done`. verdict.sh and board.sh are looked up in
# $AGENT_SCRIPTS (default: this script's directory) so tests can fake them.
# Exit codes: 0 merged, 3 not eligible (reasons on stdout, nothing merged), 1 GitHub error, unreadable data,
# failed merge (board untouched) or failed board update after the merge, 2 usage error.
set -euo pipefail

OWNER="Adamniels"
REPO_NAME="Personal-platform"
CHECK="ci-ok"
CHECK_APP="github-actions"
LABEL="needs-owner"

[ $# -eq 1 ] && [[ "$1" =~ ^[1-9][0-9]*$ ]] || { echo "usage: merge-gate.sh <pr-number>" >&2; exit 2; }
pr="$1"
scripts="${AGENT_SCRIPTS:-$(cd "$(dirname "$0")" && pwd)}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

fail() { echo "pr #$pr: $*" >&2; exit 1; }

# Every read is a plain assignment or redirect with an explicit `|| fail`, so no failure reads as a condition.
gh pr view "$pr" --repo "$OWNER/$REPO_NAME" --json headRefOid,labels,closingIssuesReferences > "$tmp/pr.json" \
  || fail "could not read the pull request"
fields="$(jq -r --arg label "$LABEL" '[.headRefOid, (.closingIssuesReferences[0].number // "-"),
  any(.labels[]; .name == $label)] | @tsv' "$tmp/pr.json")" || fail "could not read the pull request"
IFS=$'\t' read -r sha slice labelled <<<"$fields"
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || fail "unreadable head sha: $sha"
[ "$slice" = - ] || [[ "$slice" =~ ^[1-9][0-9]*$ ]] || fail "unreadable closing issue: $slice"
[ "$labelled" = true ] || [ "$labelled" = false ] || fail "unreadable labels"

reasons=""
reason() { reasons="$reasons$1"$'\n'; }

# ci-ok: one "<status> <conclusion>" line per ci-ok run on the head commit.
runs="$(gh api "repos/$OWNER/$REPO_NAME/commits/$sha/check-runs?check_name=$CHECK&per_page=100" \
  --jq ".check_runs[] | select(.name == \"$CHECK\" and .app.slug == \"$CHECK_APP\") | \"\(.status) \(.conclusion)\"")" \
  || fail "could not read the check runs of $sha"
if grep -q '^completed ' <<<"$runs" && grep '^completed ' <<<"$runs" | grep -qv '^completed success$'; then
  reason "ci-ok failed"
elif [ -z "$runs" ] || grep -qv '^completed ' <<<"$runs"; then
  reason "ci-ok is pending"
fi

# Verdict: `<approve|changes|none> <sha|0> <count> <created_at|->`.
line="$(bash "$scripts/verdict.sh" "$pr")" || fail "verdict.sh failed"
read -r kind vsha _ <<<"$line"
case "$kind" in approve|changes|none) ;; *) fail "unreadable verdict line: $line" ;; esac
[ "$kind" = approve ] || reason "verdict is not approve"
[ "$vsha" = "$sha" ] || reason "verdict is not for the head commit"

# Risk.
word=""
if [ "$slice" != - ]; then
  body="$(gh issue view "$slice" --repo "$OWNER/$REPO_NAME" --json body --jq .body)" \
    || fail "could not read slice #$slice"
  # Prints the first word of the first non-empty line of the only `## Risk` section, or nothing.
  word="$(printf '%s\n' "$body" | tr -d '\r' | awk '
    /^## Risk[[:space:]]*$/ { sections++; inside = 1; next }
    /^## / { inside = 0; next }
    inside && !found && /[^[:space:]]/ { sub(/^[[:space:]]+/, ""); split($0, w, /[[:space:]]+/); first = w[1]; found = 1 }
    END { if (sections == 1 && found) print first }')"
  word="${word%[,.]}"
fi
[ "$word" = low ] || reason "risk is not low"

[ "$labelled" = false ] || reason "needs-owner label is set"

if [ -n "$reasons" ]; then
  printf '%s' "$reasons"
  exit 3
fi

gh pr merge "$pr" --merge --match-head-commit "$sha" --repo "$OWNER/$REPO_NAME" || fail "the merge failed"
echo "merged #$pr"
bash "$scripts/board.sh" status "$slice" Done || fail "merged, but moving slice #$slice to Done failed"
