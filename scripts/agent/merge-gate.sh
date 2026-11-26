#!/usr/bin/env bash
# Merges an agent's pull request into main when, and only when, every condition below holds. This is the one
# path by which code reaches main unattended, so it fails closed: anything missing or unclear is "not eligible",
# and any GitHub read that fails is an error, never a merge.
#
#   bash scripts/agent/merge-gate.sh <pr-number>
#
# A pull request that is already merged is answered first and alone: the one reason line
#   pull request is already merged
# with exit 3, and nothing else is read or printed. A listing can hand the runner a pull request this gate merged
# a moment ago, and its slice is closed by then, so every other condition would only add noise.
#
# Conditions, each printed as its reason line on stdout when unmet (all unmet ones, in this order):
#   author is not the bot                   the PR was not opened by mac-mini-agent
#   base is not main                        the PR does not target main
#   pull request is a draft                 the PR is a draft
#   head is from another repository         the PR comes from a fork
#   touches protected paths                 a changed file (or a rename's old path) is under .github/, scripts/
#                                           or .claude/, or is the root AGENTS.md or CLAUDE.md, compared in
#                                           any letter case: these steer CI, this gate and the agents themselves
#   ci-ok is pending                        no completed `ci-ok` check run (by GitHub Actions) on the head commit
#   ci-ok failed                            a completed `ci-ok` run on the head commit concluded other than success
#   verdict is not approve                  verdict.sh does not give approve
#   verdict is not for the head commit      the verdict's sha is not the PR's head sha
#   slice is not the branch's planned issue pr-slice.sh finds no slice: the head branch starts with `<n>-` and
#                                           GitHub links exactly issue <n> of this repository, or, when GitHub
#                                           links none, the body closes exactly <n>. GitHub missed the link once,
#                                           on #471, and the gate then refused a correct pull request
#   slice is not open                       the slice issue is not open
#   risk is not low                         the slice's `## Risk` section does not start with the word `low`
#                                           (also printed when there is no planned slice to read)
#   risk record is not the owner's          the slice issue was not written by the owner, or any edit in its
#                                           whole history was made by someone else (or by an unnamed editor)
#   an owner label is set                   the PR carries owner-merge, owner-decision or owner-blocked, or
#                                           has-decision, the implementer's mark that a decision waits for the
#                                           owner. in-review is not one of them and never refuses
# The runner reads these lines: it drops the PR on `pull request is already merged`, waits whenever
# `ci-ok is pending` is among them, hands the PR to the owner as ready when the only ones are `risk is not low`,
# `touches protected paths` and `an owner label is set`, and escalates otherwise, so they are an interface.
#
# Risk is low when the first non-empty line of the slice body's first and only `## Risk` section (up to the
# next `## ` heading) has `low` as its first word, case-sensitive, allowing one trailing comma or period:
# `low, a copy change` passes; `Low`, `low-ish`, an empty or missing section, or two Risk sections do not.
# Lines inside fenced code blocks are never headings. A fence opens with three or more ` or ~ and closes only on
# a line of the same character, at least as long, with nothing after it but spaces.
# When eligible it runs `gh pr merge <pr> --merge --match-head-commit <head sha>`, so a commit pushed after the
# checks cannot slip in; then, when GitHub did not link the slice and so will not close it, `gh issue close
# <slice>` with a comment saying why; and then `board.sh status <slice> Done`. verdict.sh, board.sh and
# pr-slice.sh are looked up in $AGENT_SCRIPTS (default: this script's directory) so tests can fake them.
# Exit codes: 0 merged, 3 not eligible (reasons on stdout, nothing merged), 1 GitHub error, unreadable data,
# failed merge (board untouched), or a failed close or board update after the merge, 2 usage error.
set -euo pipefail

OWNER="Adamniels"
REPO_NAME="Personal-platform"
BOT="mac-mini-agent"
BASE="main"
CHECK="ci-ok"
CHECK_APP="github-actions"
# Any of these keeps a PR for the owner. What each one tells him is the runner's business (run.sh); here they are
# equal. The three owner labels are the runner's; has-decision is the implementer's, set when it makes a decision
# the owner must answer, and never removed.
LABELS='["owner-merge","owner-decision","owner-blocked","has-decision"]'
# Matched against the lowercased path. Passed to awk through ENVIRON, so its backslashes reach the regex as written.
PROTECTED='^(\.github|scripts|\.claude)/|^(agents|claude)\.md$'

[ $# -eq 1 ] && [[ "$1" =~ ^[1-9][0-9]*$ ]] || { echo "usage: merge-gate.sh <pr-number>" >&2; exit 2; }
pr="$1"
scripts="${AGENT_SCRIPTS:-$(cd "$(dirname "$0")" && pwd)}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

fail() { echo "pr #$pr: $*" >&2; exit 1; }

# Every read is a plain assignment or redirect with an explicit `|| fail`, so no failure reads as a condition.
gh pr view "$pr" --repo "$OWNER/$REPO_NAME" \
  --json state,headRefOid,headRefName,baseRefName,isDraft,isCrossRepository,author,changedFiles,labels,closingIssuesReferences,body \
  > "$tmp/pr.json" || fail "could not read the pull request"
# One field per line, so an empty value cannot shift the others.
jq -r --argjson labels "$LABELS" '
  if (.headRefOid | type) != "string" or (.headRefName | type) != "string" or (.baseRefName | type) != "string"
     or (.isDraft | type) != "boolean" or (.isCrossRepository | type) != "boolean"
     or (.changedFiles | type) != "number" or (.labels | type) != "array"
     or (.closingIssuesReferences | type) != "array" or (.state | type) != "string"
  then error("unexpected pull request fields") else . end
  | .headRefOid, (.author.login // "-"), .baseRefName, .isDraft, .isCrossRepository, .changedFiles,
    any(.labels[]; .name as $n | $labels | index($n) != null), .state' "$tmp/pr.json" > "$tmp/fields" || fail "could not read the pull request"
{ read -r sha; read -r author; read -r base; read -r draft; read -r cross; read -r changed
  read -r labelled; read -r state; } < "$tmp/fields" || fail "could not read the pull request"
# The slice, by the rule the runner shares: "<n> linked", "<n> body" when GitHub missed the link and so will close
# nothing, or "-".
link="$(bash "$scripts/pr-slice.sh" < "$tmp/pr.json")" || fail "could not read the pull request"
slice="${link%% *}" how="${link#* }"
# Already merged: the one reason, alone, before anything else is read. Only GitHub's own word for it counts, so a
# closed, unmerged pull request goes on to the conditions and fails at the merge like any other.
if [ "$state" = MERGED ]; then
  echo "pull request is already merged"
  exit 3
fi
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
if PROTECTED="$PROTECTED" awk -F'\t' '
     { re = ENVIRON["PROTECTED"] }
     tolower($1) ~ re || tolower($2) ~ re { hit = 1 }
     END { exit !hit }' "$tmp/files"; then
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
  # --paginate follows userContentEdits through $endCursor and prints one JSON document per page.
  gh api graphql --paginate \
    -f query="query(\$endCursor: String){repository(owner:\"$OWNER\",name:\"$REPO_NAME\"){issue(number:$slice){state body author{login} userContentEdits(first:100, after:\$endCursor){pageInfo{hasNextPage endCursor} nodes{editor{login}}}}}}" \
    --jq '.data.repository.issue' > "$tmp/issue.json" || fail "could not read slice #$slice"
  # Line 1: state or "-"; line 2: author or "-"; line 3: "owner" when every edit, on every page, is by the owner.
  # The last page must say there is no next page, or the history read is incomplete.
  jq -s -r --arg owner "$OWNER" '
    if length == 0 or any(.[]; type != "object" or (.userContentEdits.nodes | type) != "array")
       or (.[0].body | type) != "string" or .[-1].userContentEdits.pageInfo.hasNextPage != false
    then error("unreadable or incomplete issue") else . end
    | (.[0].state // "-"), (.[0].author.login // "-"),
      (if all(.[].userContentEdits.nodes[]; .editor.login == $owner) then "owner" else "other" end)' \
    "$tmp/issue.json" > "$tmp/record" || fail "could not read slice #$slice"
  { read -r issue_state; read -r issue_author; read -r issue_editors; } < "$tmp/record" \
    || fail "could not read slice #$slice"
  [ "$issue_state" = OPEN ] || reason "slice is not open"
  # Prints the first word of the first non-empty line of the only `## Risk` section, or nothing.
  word="$(jq -s -r '.[0].body' "$tmp/issue.json" | tr -d '\r' | awk '
    # fence_of(line): true when the line starts (after spaces) with a run of 3+ ` or ~, setting fc (the
    # character), fn (the run length) and frest (what follows the run).
    function fence_of(line,   c, n) {
      sub(/^[[:space:]]+/, "", line); c = substr(line, 1, 1)
      if (c != "`" && c != "~") return ""
      n = 0; while (substr(line, n + 1, 1) == c) n++
      if (n < 3) return ""
      fc = c; fn = n; frest = substr(line, n + 1); return 1
    }
    fence && fence_of($0) && fc == fch && fn >= flen && frest ~ /^[[:space:]]*$/ { fence = 0; next }
    fence { if (inside && !found && /[^[:space:]]/) { first = "fence"; found = 1 }; next }
    fence_of($0) { fence = 1; fch = fc; flen = fn; if (inside && !found) { first = "fence"; found = 1 }; next }
    /^## Risk[[:space:]]*$/ { sections++; inside = 1; next }
    /^## / { inside = 0; next }
    inside && !found && /[^[:space:]]/ { sub(/^[[:space:]]+/, ""); split($0, w, /[[:space:]]+/); first = w[1]; found = 1 }
    END { if (sections == 1 && found) print first }')" || fail "could not read the Risk section of slice #$slice"
  word="${word%[,.]}"
fi
[ "$word" = low ] || reason "risk is not low"
if [ "$slice" != - ] && { [ "$issue_author" != "$OWNER" ] || [ "$issue_editors" != owner ]; }; then
  reason "risk record is not the owner's"
fi

[ "$labelled" = false ] || reason "an owner label is set"

if [ -n "$reasons" ]; then
  printf '%s' "$reasons"
  exit 3
fi

gh pr merge "$pr" --merge --match-head-commit "$sha" --repo "$OWNER/$REPO_NAME" || fail "the merge failed"
echo "merged #$pr"
if [ "$how" = body ]; then
  gh issue close "$slice" --repo "$OWNER/$REPO_NAME" --reason completed \
    --comment "Closed by the merge gate: its pull request #$pr is merged, and GitHub did not link the two." \
    > /dev/null || fail "merged, but closing slice #$slice failed"
  echo "closed #$slice"
fi
bash "$scripts/board.sh" status "$slice" Done || fail "merged, but moving slice #$slice to Done failed"
