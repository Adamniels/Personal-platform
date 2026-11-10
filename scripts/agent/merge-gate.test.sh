#!/usr/bin/env bash
# Offline tests for merge-gate.sh. A fake `gh` on PATH serves a canned pull request, its changed files, check
# runs, PR comments and slice issue, applying any --jq expression with jq as gh would, and logs every call, so no test touches GitHub.
# The real verdict.sh reads the fake comments; board.sh is faked (AGENT_SCRIPTS) and logs into the same log.
# Run: bash scripts/agent/merge-gate.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin" "$tmp/scripts" "$tmp/data"

# Fixtures in $FAKE_DATA: pr.json (gh pr view), files.json (the PR's changed files), checks-<sha>.json (check
# runs of that commit, default none), comments.json (PR comments), issue-<n>.json (the GraphQL issue read; a
# missing file is a GraphQL error). FAKE_FAIL=<pattern> makes any call whose
# arguments match the shell pattern fail; FAKE_MERGE_FAILS=1 makes the merge fail.
cat > "$tmp/bin/gh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_GH_LOG"
if [ -n "${FAKE_FAIL:-}" ]; then
  case "$*" in $FAKE_FAIL) echo "HTTP 502: Bad Gateway" >&2; exit 1 ;; esac
fi
jqexpr="" prev=""
for a in "$@"; do [ "$prev" = --jq ] && jqexpr="$a"; prev="$a"; done
serve() { if [ -n "$jqexpr" ]; then jq -r "$jqexpr" "$1"; else cat "$1"; fi; }
case "$*" in
  "pr view 7 "*) serve "$FAKE_DATA/pr.json" ;;
  "api repos/Adamniels/Personal-platform/commits/"*"/check-runs"*)
    sha="${2#repos/Adamniels/Personal-platform/commits/}"; sha="${sha%%/*}"
    f="$FAKE_DATA/checks-$sha.json"
    [ -f "$f" ] || { echo '{"total_count":0,"check_runs":[]}' > "$FAKE_DATA/nochecks.json"; f="$FAKE_DATA/nochecks.json"; }
    serve "$f" ;;
  "api repos/Adamniels/Personal-platform/issues/7/comments --paginate") cat "$FAKE_DATA/comments.json" ;;
  "api repos/Adamniels/Personal-platform/pulls/7/files --paginate "*) serve "$FAKE_DATA/files.json" ;;
  "api graphql "*"issue(number:"*)
    n="$(printf '%s' "$*" | sed -n 's/.*issue(number:\([0-9]*\)).*/\1/p')"
    f="$FAKE_DATA/issue-$n.json"
    [ -f "$f" ] || { echo "GraphQL: Could not resolve to an issue with the number of $n." >&2; exit 1; }
    serve "$f" ;;
  "pr merge "*)
    if [ "${FAKE_MERGE_FAILS:-0}" = 1 ]; then echo "Pull request is not mergeable" >&2; exit 1; fi ;;
  *) echo "unexpected gh call: $*" >&2; exit 9 ;;
esac
FAKE
cat > "$tmp/scripts/board.sh" <<'FAKE'
#!/usr/bin/env bash
echo "board.sh $*" >> "$FAKE_GH_LOG"
echo "#$2: $3"
FAKE
ln -s "$here/verdict.sh" "$tmp/scripts/verdict.sh"
chmod +x "$tmp/bin/gh" "$tmp/scripts/board.sh"
export PATH="$tmp/bin:$PATH" FAKE_GH_LOG="$tmp/log" FAKE_DATA="$tmp/data" AGENT_SCRIPTS="$tmp/scripts"
# Never reach the real, logged-in gh: if the fake is not first on PATH, stop before any test runs.
[ "$(command -v gh)" = "$tmp/bin/gh" ] || { echo "the fake gh is not first on PATH" >&2; exit 1; }

pass=0 fail=0
check() { # name, condition result
  if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi
}

HEAD=1111111111111111111111111111111111111111
OLD=2222222222222222222222222222222222222222
SLICE=11

# pr [labels, comma separated] [slice, empty for none]: bot PR 7 into main, head $HEAD on branch "11-copy-change".
pr() {
  jq -n -c --arg sha "$HEAD" --arg labels "${1:-}" --arg slice "${2-$SLICE}" \
    '{number: 7, headRefOid: $sha, headRefName: "11-copy-change", baseRefName: "main", isDraft: false,
      isCrossRepository: false, author: {login: "mac-mini-agent"}, changedFiles: 1,
      labels: [$labels | split(",")[] | select(. != "") | {name: .}],
      closingIssuesReferences: [$slice | select(. != "") |
        {number: tonumber, repository: {name: "Personal-platform", owner: {login: "Adamniels"}}}]}' > "$FAKE_DATA/pr.json"
}
# edit <file> <jq expression>: rewrites a fixture.
edit() { jq -c "$2" "$FAKE_DATA/$1" > "$FAKE_DATA/edit.tmp" && mv "$FAKE_DATA/edit.tmp" "$FAKE_DATA/$1"; }
# files <path>...: the PR's changed files (one entry per path; `old=>new` is a rename); sets changedFiles to match.
files() {
  local list='[]' f
  for f in "$@"; do
    case "$f" in
      *"=>"*) list="$(jq -c --arg o "${f%%=>*}" --arg n "${f#*=>}" '. + [{filename: $n, previous_filename: $o, status: "renamed"}]' <<<"$list")" ;;
      *) list="$(jq -c --arg n "$f" '. + [{filename: $n, status: "modified"}]' <<<"$list")" ;;
    esac
  done
  echo "$list" > "$FAKE_DATA/files.json"
  edit pr.json ".changedFiles = $#"
}
# checks <sha> [<name> <status> <conclusion> [--app <slug>]]...: the check runs of a commit, by GitHub Actions
# unless --app follows a run.
checks() {
  local sha="$1"; shift
  local runs='[]'
  while [ $# -ge 3 ]; do
    runs="$(jq -c --arg n "$1" --arg s "$2" --arg c "$3" --arg app github-actions \
      '. + [{name: $n, status: $s, conclusion: (if $c == "null" then null else $c end), app: {slug: $app}}]' <<<"$runs")"
    shift 3; [ $# -ge 1 ] && [ "$1" = --app ] && { runs="$(jq -c --arg app "$2" '.[-1].app.slug = $app' <<<"$runs")"; shift 2; }
  done
  jq -n -c --argjson r "$runs" '{total_count: ($r | length), check_runs: $r}' > "$FAKE_DATA/checks-$sha.json"
}
# verdict <approve|changes> <sha>: one verdict comment by the bot.
verdict() {
  jq -n -c --arg b "<!-- verdict: $1 sha=$2 -->
Review text." '[{id: 1, user: {login: "mac-mini-agent"}, created_at: "2026-11-01T10:00:00Z",
                 updated_at: "2026-11-01T10:00:00Z", body: $b}]' > "$FAKE_DATA/comments.json"
}
# risk <body of the slice issue>: an issue written by the owner and never edited.
risk() {
  jq -n -c --arg b "$1" '{data: {repository: {issue: {body: $b, author: {login: "Adamniels"}, editor: null,
    lastEditedAt: null}}}}' > "$FAKE_DATA/issue-$SLICE.json"
}
body_low="## What

A slice.

## Risk

low, a copy change

## Notes

none"

# good: every condition holds.
good() {
  rm -f "$FAKE_DATA"/*.json
  pr; files docs/README.md; checks "$HEAD" ci-ok completed success; verdict approve "$HEAD"; risk "$body_low"
}
run() { : > "$FAKE_GH_LOG"; out="$(bash "$here/merge-gate.sh" "$@" 2>"$tmp/err")"; code=$?; err="$(cat "$tmp/err")"; }
merged() { grep -q 'pr merge' "$FAKE_GH_LOG"; }
# not_eligible <reason>...: exit 3, stdout is exactly these reason lines, and nothing was merged or moved.
not_eligible() {
  local want; want="$(printf '%s\n' "$@")"
  [ "$code" = 3 ] && [ "$out" = "${want%$'\n'}" ] && ! merged && ! grep -q 'board.sh' "$FAKE_GH_LOG"
}

# Usage.
run; [ "$code" = 2 ] && [ ! -s "$FAKE_GH_LOG" ]
check "no PR number is a usage error" $?
run 7x; [ "$code" = 2 ] && [ ! -s "$FAKE_GH_LOG" ]
check "a non-numeric PR number is a usage error" $?

# Every condition holds.
good
run 7
[ "$code" = 0 ] && grep -q "^pr merge 7 --merge" "$FAKE_GH_LOG" && grep -q -- "--match-head-commit $HEAD" "$FAKE_GH_LOG" \
  && grep -qx "board.sh status $SLICE Done" "$FAKE_GH_LOG"
check "every condition holds: merges against the head commit and moves the slice to Done" $?
[ "$(grep -n 'pr merge' "$FAKE_GH_LOG" | cut -d: -f1)" -lt "$(grep -n 'board.sh' "$FAKE_GH_LOG" | cut -d: -f1)" ]
check "the board moves only after the merge" $?
grep -q "commits/$HEAD/check-runs.* --paginate" "$FAKE_GH_LOG" && grep -q "issue(number:$SLICE)" "$FAKE_GH_LOG" \
  && grep -q "pulls/7/files --paginate" "$FAKE_GH_LOG"
check "reads the head commit's check runs (paginated), the PR's files (paginated) and the slice issue" $?

# Verdict.
good; verdict approve "$OLD"
run 7; not_eligible "verdict is not for the head commit"
check "an approve for an older commit exits 3: not for the head commit" $?
good; verdict changes "$HEAD"
run 7; not_eligible "verdict is not approve"
check "a changes verdict exits 3: not approve" $?
good; verdict changes "$OLD"
run 7; not_eligible "verdict is not approve" "verdict is not for the head commit"
check "a changes verdict for an older commit gives both verdict reasons" $?
good; echo '[]' > "$FAKE_DATA/comments.json"
run 7; [ "$code" = 3 ] && grep -qx "verdict is not approve" <<<"$out" && ! merged
check "no verdict exits 3: not approve" $?
good; jq -n -c --arg b "> <!-- verdict: approve sha=$HEAD -->" \
  '[{id: 1, user: {login: "mac-mini-agent"}, created_at: "2026-11-01T10:00:00Z", body: $b}]' > "$FAKE_DATA/comments.json"
run 7; [ "$code" = 3 ] && grep -qx "verdict is not approve" <<<"$out" && ! merged
check "a malformed approve marker is no verdict" $?
good; jq -n -c --arg b "<!-- verdict: approve sha=$HEAD -->" \
  '[{id: 1, user: {login: "Adamniels"}, created_at: "2026-11-01T10:00:00Z", body: $b}]' > "$FAKE_DATA/comments.json"
run 7; [ "$code" = 3 ] && grep -qx "verdict is not approve" <<<"$out" && ! merged
check "an approve marker by another author is no verdict" $?

# Risk.
for bad in \
  "## What

no risk section" \
  "## Risk

High" \
  "## Risk

high" \
  "## Risk

low-ish" \
  "## Risk

Low" \
  "## Risk

**low**" \
  "## Risk

lowest" \
  "## Risk

low; mostly" \
  "## Risk

## Notes

low" \
  "## Risk" \
  "### Risk

low" \
  "## Risks

low" \
  "## What

low

## Risk

medium" \
  "## Risk

low

## Risk

high" \
  "## What

\`\`\`
## Risk
low
\`\`\`" \
  "## Risk

\`\`\`
low
\`\`\`" \
  "## Risk

low

\`\`\`
## Risk
high
\`\`\`

## Risk

high"; do
  good; risk "$bad"
  run 7; not_eligible "risk is not low"; r=$?
  check "risk is not low: $(printf '%s' "$bad" | tr '\n' '|')" $r
done
for ok_body in \
  "$body_low" \
  "## Risk
low" \
  "## Risk

low." \
  "## Risk

   low because only docs change" \
  "## Risk

low
## Notes
high" \
  "$(printf '## What\r\n\r\nx\r\n\r\n## Risk\r\n\r\nlow, a copy change\r\n')"; do
  good; risk "$ok_body"
  run 7; [ "$code" = 0 ] && merged; r=$?
  check "risk is low: $(printf '%s' "$ok_body" | tr '\r\n' '~|')" $r
done
good; risk "## Risk

low

\`\`\`
## Notes
## Risk
\`\`\`
"
run 7; [ "$code" = 0 ] && merged
check "headings inside a fenced block neither end nor repeat the Risk section" $?

# The slice is the one closing issue in this repository whose number starts the head branch name.
good; pr "" ""
run 7; not_eligible "slice is not the branch's planned issue" "risk is not low"
check "a PR without a closing issue: slice is not the branch's planned issue" $?
grep -q 'issue(number' "$FAKE_GH_LOG"; [ $? != 0 ]
check "a PR without a planned slice reads no issue" $?
good; edit pr.json '.closingIssuesReferences += [{number: 12, repository: {name: "Personal-platform", owner: {login: "Adamniels"}}}]'
run 7; not_eligible "slice is not the branch's planned issue" "risk is not low"
check "two closing issues: slice is not the branch's planned issue" $?
good; edit pr.json '.headRefName = "12-copy-change"'
run 7; not_eligible "slice is not the branch's planned issue" "risk is not low"
check "a closing issue other than the branch's number is refused" $?
for branch in copy-change 011-copy-change 11copy 11 x11-copy; do
  good; edit pr.json ".headRefName = \"$branch\""
  run 7; not_eligible "slice is not the branch's planned issue" "risk is not low"; r=$?
  check "a branch name without a leading <n>- is refused: $branch" $r
done
good; edit pr.json '.closingIssuesReferences[0].repository.name = "Other"'
run 7; not_eligible "slice is not the branch's planned issue" "risk is not low"
check "a closing issue in another repository is refused" $?
good; edit pr.json '.closingIssuesReferences[0].repository.owner.login = "someone"'
run 7; not_eligible "slice is not the branch's planned issue" "risk is not low"
check "a closing issue under another owner is refused" $?

# The risk record must be the owner's: written by the owner and, if edited, last edited by the owner.
good; edit "issue-$SLICE.json" '.data.repository.issue.author.login = "mac-mini-agent"'
run 7; not_eligible "risk record is not the owner's"
check "a slice written by someone else: risk record is not the owner's" $?
good; edit "issue-$SLICE.json" '.data.repository.issue.author = null'
run 7; not_eligible "risk record is not the owner's"
check "a slice with no author: risk record is not the owner's" $?
good; edit "issue-$SLICE.json" '.data.repository.issue.editor = {login: "mac-mini-agent"} | .data.repository.issue.lastEditedAt = "2026-11-02T10:00:00Z"'
run 7; not_eligible "risk record is not the owner's"
check "a slice last edited by someone else: risk record is not the owner's" $?
good; edit "issue-$SLICE.json" '.data.repository.issue.editor = {login: "Adamniels"} | .data.repository.issue.lastEditedAt = "2026-11-02T10:00:00Z"'
run 7; [ "$code" = 0 ] && merged
check "a slice last edited by the owner passes" $?
good; edit "issue-$SLICE.json" '.data.repository.issue.editor = null | .data.repository.issue.lastEditedAt = "2026-11-02T10:00:00Z"'
run 7; not_eligible "risk record is not the owner's"
check "an edited slice whose editor is unknown: risk record is not the owner's" $?

# Protected paths.
for f in .github/workflows/ci.yml scripts/agent/merge-gate.sh scripts/ci/ci-ok.sh .claude/skills/x/SKILL.md \
  ".github/x=>docs/x" "docs/x=>scripts/x"; do
  good; files docs/README.md "$f"
  run 7; not_eligible "touches protected paths"; r=$?
  check "touches protected paths: $f" $r
done
for f in docs/scripts/x.md core/.github.md src/claude/x github/x; do
  good; files "$f"
  run 7; [ "$code" = 0 ] && merged; r=$?
  check "not a protected path: $f" $r
done
good; files docs/a.md; edit pr.json '.changedFiles = 2'
run 7; [ "$code" = 1 ] && [ -z "$out" ] && ! merged
check "a file list shorter than changedFiles exits 1 without merging" $?

# The pull request itself.
good; edit pr.json '.author.login = "Adamniels"'
run 7; not_eligible "author is not the bot"
check "a PR by someone else: author is not the bot" $?
good; edit pr.json '.baseRefName = "release"'
run 7; not_eligible "base is not main"
check "a PR into another branch: base is not main" $?
good; edit pr.json '.isDraft = true'
run 7; not_eligible "pull request is a draft"
check "a draft PR: pull request is a draft" $?
good; edit pr.json '.isCrossRepository = true'
run 7; not_eligible "head is from another repository"
check "a fork PR: head is from another repository" $?
good; edit pr.json 'del(.isDraft)'
run 7; [ "$code" = 1 ] && ! merged
check "a missing draft flag exits 1 without merging" $?

# ci-ok.
good; checks "$HEAD" ci-ok completed failure
run 7; not_eligible "ci-ok failed"
check "ci-ok failure exits 3: ci-ok failed" $?
for c in cancelled timed_out skipped neutral action_required stale; do
  good; checks "$HEAD" ci-ok completed "$c"
  run 7; not_eligible "ci-ok failed"
  check "ci-ok $c exits 3: ci-ok failed" $?
done
good; rm -f "$FAKE_DATA/checks-$HEAD.json"
run 7; not_eligible "ci-ok is pending"
check "ci-ok missing exits 3: ci-ok is pending" $?
good; checks "$HEAD" ci-ok in_progress null
run 7; not_eligible "ci-ok is pending"
check "ci-ok in progress exits 3: ci-ok is pending" $?
good; checks "$HEAD" ci-ok queued null
run 7; not_eligible "ci-ok is pending"
check "ci-ok queued exits 3: ci-ok is pending" $?
good; checks "$HEAD" scripts completed success
run 7; not_eligible "ci-ok is pending"
check "other checks passing without ci-ok exits 3: ci-ok is pending" $?
good; rm -f "$FAKE_DATA/checks-$HEAD.json"; checks "$OLD" ci-ok completed success
run 7; not_eligible "ci-ok is pending"
check "ci-ok success on another commit does not count" $?
good; checks "$HEAD" ci-ok completed success --app someone-elses-app
run 7; not_eligible "ci-ok is pending"
check "a ci-ok check run from another app does not count" $?
good; checks "$HEAD" ci-ok completed success ci-ok completed failure
run 7; not_eligible "ci-ok failed"
check "any failed ci-ok run on the head commit fails" $?
good; checks "$HEAD" ci-ok completed success ci-ok in_progress null
run 7; not_eligible "ci-ok is pending"
check "any unfinished ci-ok run on the head commit is pending" $?

# needs-owner.
good; pr needs-owner
run 7; not_eligible "needs-owner label is set"
check "needs-owner exits 3" $?
good; pr "ready,needs-owner"
run 7; not_eligible "needs-owner label is set"
check "needs-owner among other labels exits 3" $?
good; pr "needs-owner-later"
run 7; [ "$code" = 0 ] && merged
check "a label that only starts with needs-owner is not needs-owner" $?

# Every unmet condition is reported, in a stable order.
rm -f "$FAKE_DATA"/*.json
pr needs-owner; files .github/x; checks "$HEAD" ci-ok completed failure; verdict changes "$OLD"; risk "## Risk

high"
edit pr.json '.author.login = "x" | .baseRefName = "dev" | .isDraft = true | .isCrossRepository = true'
edit "issue-$SLICE.json" '.data.repository.issue.author.login = "x"'
run 7; not_eligible "author is not the bot" "base is not main" "pull request is a draft" \
  "head is from another repository" "touches protected paths" "ci-ok failed" "verdict is not approve" \
  "verdict is not for the head commit" "risk is not low" "risk record is not the owner's" "needs-owner label is set"
check "every unmet condition is reported" $?
good; edit pr.json '.headRefName = "x"'; checks "$HEAD" ci-ok in_progress null
run 7; not_eligible "ci-ok is pending" "slice is not the branch's planned issue" "risk is not low"
check "the slice reason sits between the verdict and the risk reasons" $?

# GitHub failures exit 1, never a merge.
for pattern in "pr view *" "api *check-runs*" "api *comments*" "api graphql *" "api *pulls/7/files*"; do
  good
  FAKE_FAIL="$pattern" run 7; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ] && ! merged && ! grep -q board.sh "$FAKE_GH_LOG"
  check "a failing read ($pattern) exits 1 without merging" $?
done
good; rm -f "$FAKE_DATA/issue-$SLICE.json"
run 7; [ "$code" = 1 ] && ! merged
check "an unreadable slice issue exits 1 without merging" $?
good; echo 'not json' > "$FAKE_DATA/pr.json"
run 7; [ "$code" = 1 ] && [ -z "$out" ] && ! merged
check "an unreadable pull request exits 1 without merging" $?
good; jq -c '.headRefOid = "not-a-sha"' "$FAKE_DATA/pr.json" > "$FAKE_DATA/p" && mv "$FAKE_DATA/p" "$FAKE_DATA/pr.json"
run 7; [ "$code" = 1 ] && ! merged
check "a malformed head sha exits 1 without merging" $?

# A failing merge exits 1 and leaves the board alone.
good
FAKE_MERGE_FAILS=1 run 7; [ "$code" = 1 ] && grep -q 'pr merge' "$FAKE_GH_LOG" && ! grep -q board.sh "$FAKE_GH_LOG"
check "a failing merge exits 1 and does not touch the board" $?

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
