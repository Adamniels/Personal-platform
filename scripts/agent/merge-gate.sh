#!/usr/bin/env bash
# Merges an agent's pull request into main when, and only when, every condition below holds. This is the one
# path by which code reaches main unattended, so it fails closed: anything missing or unclear is "not eligible",
# and any GitHub read that fails is an error, never a merge.
#
#   bash scripts/agent/merge-gate.sh <pr-number>
#
# Conditions, each printed as its reason line on stdout when unmet (all unmet ones, in this order):
#   author is not the bot                   the PR was not opened by mac-mini-agent
#   base is not main                        the PR does not target main
#   pull request is a draft                 the PR is a draft
#   head is from another repository         the PR comes from a fork
#   touches protected paths                 a changed file (or a rename's old path) is under .github/, scripts/
#                                           or .claude/, which steer CI, this gate and the agents themselves
#   ci-ok is pending                        no completed `ci-ok` check run (by GitHub Actions) on the head commit
#   ci-ok failed                            a completed `ci-ok` run on the head commit concluded other than success
#   verdict is not approve                  verdict.sh does not give approve
#   verdict is not for the head commit      the verdict's sha is not the PR's head sha
#   slice is not the branch's planned issue the PR does not close exactly one issue, in this repository, whose
#                                           number starts the head branch name (`<n>-...`)
#   risk is not low                         the slice's `## Risk` section does not start with the word `low`
#                                           (also printed when there is no planned slice to read)
#   risk record is not the owner's          the slice issue was not written by the owner, or was last edited by
#                                           someone else (or by an editor GitHub does not name)
#   needs-owner label is set                the PR carries the needs-owner label
# The runner reads these lines: it waits on `ci-ok is pending` alone, asks the owner on `risk is not low`
# alone, and escalates otherwise, so they are an interface.
#
# Risk is low when the first non-empty line of the slice body's first and only `## Risk` section (up to the
# next `## ` heading) has `low` as its first word, case-sensitive, allowing one trailing comma or period:
# `low, a copy change` passes; `Low`, `low-ish`, an empty or missing section, or two Risk sections do not.
# Lines inside fenced code blocks (``` or ~~~) are never headings.
# When eligible it runs `gh pr merge <pr> --merge --match-head-commit <head sha>`, so a commit pushed after the
# checks cannot slip in, and then `board.sh status <slice> Done`. verdict.sh and board.sh are looked up in
# $AGENT_SCRIPTS (default: this script's directory) so tests can fake them.
# Exit codes: 0 merged, 3 not eligible (reasons on stdout, nothing merged), 1 GitHub error, unreadable data,
# failed merge (board untouched) or failed board update after the merge, 2 usage error.
set -euo pipefail

OWNER="Adamniels"
REPO_NAME="Personal-platform"
BOT="mac-mini-agent"
BASE="main"
CHECK="ci-ok"
CHECK_APP="github-actions"
LABEL="needs-owner"
PROTECTED='^(\.github|scripts|\.claude)/'

[ $# -eq 1 ] && [[ "$1" =~ ^[1-9][0-9]*$ ]] || { echo "usage: merge-gate.sh <pr-number>" >&2; exit 2; }
pr="$1"
scripts="${AGENT_SCRIPTS:-$(cd "$(dirname "$0")" && pwd)}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

fail() { echo "pr #$pr: $*" >&2; exit 1; }

# Every read is a plain assignment or redirect with an explicit `|| fail`, so no failure reads as a condition.
gh pr view "$pr" --repo "$OWNER/$REPO_NAME" \
  --json headRefOid,headRefName,baseRefName,isDraft,isCrossRepository,author,changedFiles,labels,closingIssuesReferences \
  > "$tmp/pr.json" || fail "could not read the pull request"
# One field per line, so an empty value cannot shift the others. The slice is "-" unless the PR closes exactly
# one issue, in this repository, whose number is the head branch's leading `<n>-`.
jq -r --arg owner "$OWNER" --arg repo "$REPO_NAME" --arg label "$LABEL" '
  if (.headRefOid | type) != "string" or (.headRefName | type) != "string" or (.baseRefName | type) != "string"
     or (.isDraft | type) != "boolean" or (.isCrossRepository | type) != "boolean"
     or (.changedFiles | type) != "number" or (.labels | type) != "array"
     or (.closingIssuesReferences | type) != "array"
  then error("unexpected pull request fields") else . end
  | ((.headRefName | capture("^(?<n>[1-9][0-9]*)-").n) // "-") as $branch
  | .closingIssuesReferences as $refs
  | .headRefOid, (.author.login // "-"), .baseRefName, .isDraft, .isCrossRepository, .changedFiles,
    (if ($refs | length) == 1 and $refs[0].repository.owner.login == $owner and $refs[0].repository.name == $repo
        and ($refs[0].number | tostring) == $branch then $branch else "-" end),
    any(.labels[]; .name == $label)' "$tmp/pr.json" > "$tmp/fields" || fail "could not read the pull request"
{ read -r sha; read -r author; read -r base; read -r draft; read -r cross; read -r changed; read -r slice
  read -r labelled; } < "$tmp/fields" || fail "could not read the pull request"
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || fail "unreadable head sha: $sha"
[[ "$changed" =~ ^[0-9]+$ ]] || fail "unreadable changed file count: $changed"
[ "$slice" = - ] || [[ "$slice" =~ ^[1-9][0-9]*$ ]] || fail "unreadable slice: $slice"

reasons=""
reason() { reasons="$reasons$1"$'\n'; }

[ "$author" = "$BOT" ] || reason "author is not the bot"
[ "$base" = "$BASE" ] || reason "base is not main"
[ "$draft" = false ] || reason "pull request is a draft"
[ "$cross" = false ] || reason "head is from another repository"

# Changed files: one "<filename>\t<previous filename or empty>" line per file, every page.
gh api "repos/$OWNER/$REPO_NAME/pulls/$pr/files" --paginate \
  --jq '.[] | [.filename, (.previous_filename // "")] | @tsv' > "$tmp/files" || fail "could not read the changed files"
listed="$(wc -l < "$tmp/files" | tr -d ' ')"
# GitHub lists at most 3000 files; a short list could hide a protected path.
[ "$listed" = "$changed" ] || fail "the file list has $listed of $changed changed files"
if awk -F'\t' -v re="$PROTECTED" '$1 ~ re || $2 ~ re { hit = 1 } END { exit !hit }' "$tmp/files"; then
  reason "touches protected paths"
fi

# ci-ok: one "<status> <conclusion>" line per ci-ok run on the head commit.
runs="$(gh api "repos/$OWNER/$REPO_NAME/commits/$sha/check-runs?check_name=$CHECK&per_page=100" --paginate \
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

# Slice and risk. Without a planned slice there is no risk record to read, so risk is not low either.
word=""
if [ "$slice" = - ]; then
  reason "slice is not the branch's planned issue"
else
  gh api graphql \
    -f query="query{repository(owner:\"$OWNER\",name:\"$REPO_NAME\"){issue(number:$slice){body author{login} editor{login} lastEditedAt}}}" \
    --jq '.data.repository.issue' > "$tmp/issue.json" || fail "could not read slice #$slice"
  # Line 1: author or "-"; line 2: "owner" when never edited or last edited by the owner, else "other".
  jq -r --arg owner "$OWNER" '
    if type != "object" or (.body | type) != "string" then error("unreadable issue") else . end
    | (.author.login // "-"),
      (if (.editor == null and .lastEditedAt == null) or .editor.login == $owner then "owner" else "other" end)' \
    "$tmp/issue.json" > "$tmp/record" || fail "could not read slice #$slice"
  { read -r issue_author; read -r issue_editor; } < "$tmp/record" || fail "could not read slice #$slice"
  # Prints the first word of the first non-empty line of the only `## Risk` section, or nothing.
  word="$(jq -r .body "$tmp/issue.json" | tr -d '\r' | awk '
    /^[[:space:]]*(```|~~~)/ { fence = !fence; if (inside && !found) { first = "fence"; found = 1 }; next }
    !fence && /^## Risk[[:space:]]*$/ { sections++; inside = 1; next }
    !fence && /^## / { inside = 0; next }
    inside && !found && /[^[:space:]]/ { sub(/^[[:space:]]+/, ""); split($0, w, /[[:space:]]+/); first = w[1]; found = 1 }
    END { if (sections == 1 && found) print first }')" || fail "could not read the Risk section of slice #$slice"
  word="${word%[,.]}"
fi
[ "$word" = low ] || reason "risk is not low"
if [ "$slice" != - ] && { [ "$issue_author" != "$OWNER" ] || [ "$issue_editor" != owner ]; }; then
  reason "risk record is not the owner's"
fi

[ "$labelled" = false ] || reason "needs-owner label is set"

if [ -n "$reasons" ]; then
  printf '%s' "$reasons"
  exit 3
fi

gh pr merge "$pr" --merge --match-head-commit "$sha" --repo "$OWNER/$REPO_NAME" || fail "the merge failed"
echo "merged #$pr"
bash "$scripts/board.sh" status "$slice" Done || fail "merged, but moving slice #$slice to Done failed"
