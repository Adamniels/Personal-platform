#!/usr/bin/env bash
# Offline tests for pending.sh. A fake `gh` on PATH prints canned pull requests and comments, a fake
# verdict.sh and next.sh in a temp dir (AGENT_SCRIPTS) print canned lines, and the fake gh logs every call, so
# no test touches GitHub. Run: bash scripts/agent/pending.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin" "$tmp/scripts" "$tmp/data"

# prs.json is what `gh pr list` prints; comments-, inline- and reviews-<pr>.json are the three owner-input
# endpoints (default []); FAKE_FAILS_ON=<text> fails any call containing it. Old note: prs.json is what `gh pr list` prints; comments-<pr>.json is what the comments endpoint prints (default []);
# FAKE_FAILS=1 makes the fake fail.
cat > "$tmp/bin/gh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_GH_LOG"
if [ "${FAKE_FAILS:-0}" = 1 ]; then echo "HTTP 502: Bad Gateway" >&2; exit 1; fi
if [ -n "${FAKE_FAILS_ON:-}" ] && [[ "$*" == *"$FAKE_FAILS_ON"* ]]; then echo "HTTP 502: Bad Gateway" >&2; exit 1; fi
canned() { if [ -f "$FAKE_DATA/$1" ]; then cat "$FAKE_DATA/$1"; else echo '[]'; fi; }
case "$*" in
  "pr list "*) cat "$FAKE_DATA/prs.json" ;;
  "api repos/Adamniels/Personal-platform/issues/"*"/comments"*)
    n="${2#repos/Adamniels/Personal-platform/issues/}"; canned "comments-${n%%/*}.json" ;;
  "api repos/Adamniels/Personal-platform/pulls/"*"/comments"*)
    n="${2#repos/Adamniels/Personal-platform/pulls/}"; canned "inline-${n%%/*}.json" ;;
  "api repos/Adamniels/Personal-platform/pulls/"*"/reviews"*)
    n="${2#repos/Adamniels/Personal-platform/pulls/}"; canned "reviews-${n%%/*}.json" ;;
  *) echo "unexpected gh call: $*" >&2; exit 9 ;;
esac
FAKE
# verdict-<pr> holds the canned line; the word ERROR makes the fake exit 1.
cat > "$tmp/scripts/verdict.sh" <<'FAKE'
#!/usr/bin/env bash
f="$FAKE_DATA/verdict-$1"
if [ -f "$f" ] && [ "$(cat "$f")" = ERROR ]; then echo "HTTP 502: Bad Gateway" >&2; exit 1; fi
if [ -f "$f" ]; then cat "$f"; else echo "none 0 0 -"; fi
FAKE
# next.sh --machine prints "inprogress <n>" for each number in "inflight" and "next <n>" for the one in "next";
# any other call fails, so pending.sh cannot parse display text or read the board twice. "nextfail" makes it fail.
cat > "$tmp/scripts/next.sh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_GH_LOG"
[ "${1:-}" = --machine ] && [ $# -eq 1 ] || { echo "unexpected next.sh call: $*" >&2; exit 9; }
if [ -f "$FAKE_DATA/nextfail" ]; then echo "HTTP 502: Bad Gateway" >&2; exit 1; fi
if [ -f "$FAKE_DATA/inflight" ]; then while read -r n; do echo "inprogress $n"; done < "$FAKE_DATA/inflight"; fi
if [ -f "$FAKE_DATA/next" ]; then echo "next $(cat "$FAKE_DATA/next")"; fi
FAKE
chmod +x "$tmp/bin/gh" "$tmp/scripts/verdict.sh" "$tmp/scripts/next.sh"
export PATH="$tmp/bin:$PATH" FAKE_GH_LOG="$tmp/log" FAKE_DATA="$tmp/data" AGENT_SCRIPTS="$tmp/scripts"
# Never reach the real, logged-in gh: if the fake is not first on PATH, stop before any test runs.
[ "$(command -v gh)" = "$tmp/bin/gh" ] || { echo "the fake gh is not first on PATH" >&2; exit 1; }

pass=0 fail=0
check() { # name, condition result
  if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi
}

committed=2026-11-01T10:00:00Z   # every head commit
vtime=2026-11-01T11:00:00Z       # every verdict
late=2026-11-01T12:00:00Z        # after both
between=2026-11-01T10:30:00Z     # after the commit, before the verdict
early=2026-11-01T09:00:00Z       # before both

# pr <number> <slice> [needs-owner]: writes the fragment for an open bot PR with head sha "sha<number>".
pr() {
  jq -n -c --argjson n "$1" --argjson s "$2" --arg label "${3:-}" --arg d "$committed" \
    '{number: $n, headRefOid: "sha\($n)", headRefName: "branch-\($n)", labels: [$label | select(. != "") | {name: .}],
      closingIssuesReferences: [{number: $s}],
      commits: [{committedDate: "2026-10-30T10:00:00Z"}, {committedDate: $d}]}' > "$FAKE_DATA/pr-$1.json"
}
# setprs <number>...: the PRs gh lists, in the order given.
setprs() {
  rm -f "$FAKE_DATA"/verdict-* "$FAKE_DATA"/comments-* "$FAKE_DATA"/inline-* "$FAKE_DATA"/reviews-* "$FAKE_DATA"/nextfail "$FAKE_DATA"/inflight "$FAKE_DATA"/next
  local files=() n
  for n in "$@"; do files+=("$FAKE_DATA/pr-$n.json"); done
  if [ ${#files[@]} -eq 0 ]; then echo '[]' > "$FAKE_DATA/prs.json"; else jq -s -c . "${files[@]}" > "$FAKE_DATA/prs.json"; fi
}
# verdict <pr> <approve|changes|none> <count> [sha]: the verdict line for a PR, by default for its head.
verdict() { echo "$2 ${4:-sha$1} $3 $vtime" > "$FAKE_DATA/verdict-$1"; }
# comment <pr> <author> <created_at>
comment() { jq -n -c --arg u "$2" --arg t "$3" '[{user: {login: $u}, created_at: $t}]' > "$FAKE_DATA/comments-$1.json"; }
run() { : > "$FAKE_GH_LOG"; out="$(bash "$here/pending.sh" 2>"$tmp/err")"; code=$?; err="$(cat "$tmp/err")"; }

# One scenario holds every kind of work; removing the top item each time shows the next priority.
pr 1 11; pr 2 12; pr 3 13; pr 4 14
setprs 1 2 3 4
verdict 1 approve 0
verdict 3 changes 4
verdict 4 changes 1
echo 15 > "$FAKE_DATA/inflight"
echo 16 > "$FAKE_DATA/next"
run; [ "$code" = 0 ] && [ "$out" = "fix 4 14" ]
check "fix beats cap, review, merge, resume and new" $?
setprs 1 2 3; verdict 1 approve 0; verdict 3 changes 4
echo 15 > "$FAKE_DATA/inflight"; echo 16 > "$FAKE_DATA/next"
run; [ "$code" = 0 ] && [ "$out" = "cap 3 13" ]
check "cap beats review, merge, resume and new" $?
setprs 1 2; verdict 1 approve 0
echo 15 > "$FAKE_DATA/inflight"; echo 16 > "$FAKE_DATA/next"
run; [ "$code" = 0 ] && [ "$out" = "review 2 12" ]
check "review beats merge, resume and new" $?
setprs 1; verdict 1 approve 0
echo 15 > "$FAKE_DATA/inflight"; echo 16 > "$FAKE_DATA/next"
run; [ "$code" = 0 ] && [ "$out" = "merge 1 11" ]
check "merge beats resume and new" $?
setprs
echo 15 > "$FAKE_DATA/inflight"; echo 16 > "$FAKE_DATA/next"
run; [ "$code" = 0 ] && [ "$out" = "resume 15" ]
check "resume beats new" $?
echo 16 > "$FAKE_DATA/next"; rm -f "$FAKE_DATA/inflight"
run; [ "$code" = 0 ] && [ "$out" = "new 16" ]
check "new when nothing else is pending" $?
rm -f "$FAKE_DATA/next"
run; [ "$code" = 0 ] && [ -z "$out" ] && [ -z "$err" ]
check "idle prints nothing and exits 0" $?

# Round cap boundary.
setprs 1; verdict 1 changes 3
run; [ "$out" = "fix 1 11" ]
check "three changes verdicts still give fix" $?
verdict 1 changes 4
run; [ "$out" = "cap 1 11" ]
check "four changes verdicts give cap" $?

# Verdicts for another commit do not count.
setprs 1; verdict 1 approve 0 oldsha
run; [ "$out" = "review 1 11" ]
check "a verdict for an older commit means review" $?
verdict 1 changes 1 oldsha
run; [ "$out" = "review 1 11" ]
check "a changes verdict for an older commit means review" $?

# Within a class the lowest PR number comes first.
setprs 2 1; verdict 1 approve 0; verdict 2 approve 0
run; [ "$out" = "merge 1 11" ]
check "ties go to the lowest PR number" $?

# Owner comments.
setprs 1; verdict 1 approve 0; comment 1 Adamniels "$late"
run; [ "$out" = "fix 1 11" ]
check "an owner comment newer than an approve verdict gives fix" $?
comment 1 Adamniels "$between"
run; [ "$out" = "merge 1 11" ]
check "an owner comment older than the verdict does not" $?
verdict 1 approve 0; comment 1 someone-else "$late"
run; [ "$out" = "merge 1 11" ]
check "a comment by anyone else is ignored" $?
echo "none 0 0 -" > "$FAKE_DATA/verdict-1"; comment 1 Adamniels "$late"
run; [ "$out" = "fix 1 11" ]
check "with no verdict, an owner comment after the commit gives fix" $?
comment 1 Adamniels "$early"
run; [ "$out" = "review 1 11" ]
check "an owner comment before the head commit does not" $?
verdict 1 approve 0; comment 1 Adamniels "$between"
jq -c '.[0].commits[-1].committedDate = "2026-11-01T10:45:00Z"' "$FAKE_DATA/prs.json" > "$FAKE_DATA/p" && mv "$FAKE_DATA/p" "$FAKE_DATA/prs.json"
run; [ "$out" = "merge 1 11" ]
check "an owner comment before the head commit does not, even with a verdict" $?

# needs-owner.
pr 5 15 needs-owner
setprs 5; verdict 5 approve 0
run; [ "$code" = 0 ] && [ -z "$out" ]
check "a needs-owner PR without a new owner comment yields nothing (approve)" $?
verdict 5 changes 1
run; [ -z "$out" ]
check "a needs-owner PR yields nothing (changes)" $?
echo "none 0 0 -" > "$FAKE_DATA/verdict-5"
run; [ -z "$out" ]
check "a needs-owner PR yields nothing (no verdict)" $?
verdict 5 approve 0; comment 5 Adamniels "$late"
run; [ "$out" = "fix 5 15" ]
check "a needs-owner PR with a new owner comment gives fix" $?
setprs 5 1; verdict 5 approve 0; verdict 1 approve 0; echo 16 > "$FAKE_DATA/next"
run; [ "$out" = "merge 1 11" ]
check "a skipped needs-owner PR does not hide lower work" $?

# Resume only for In progress slices without an open PR.
pr 1 11; setprs 1; verdict 1 approve 0; echo 11 > "$FAKE_DATA/inflight"
run; [ "$out" = "merge 1 11" ]
check "an In progress slice with an open PR is not resumed" $?

# Reviews and inline comments count as owner input too.
setprs 1; verdict 1 approve 0
jq -n -c --arg t "$late" '[{user: {login: "Adamniels"}, state: "COMMENTED", submitted_at: $t}]' > "$FAKE_DATA/reviews-1.json"
run; [ "$out" = "fix 1 11" ]
check "an owner review newer than the verdict gives fix" $?
jq -n -c --arg t "$late" '[{user: {login: "Adamniels"}, state: "PENDING", submitted_at: null}]' > "$FAKE_DATA/reviews-1.json"
run; [ "$out" = "merge 1 11" ]
check "a pending owner review is ignored" $?
jq -n -c --arg t "$between" '[{user: {login: "Adamniels"}, state: "APPROVED", submitted_at: $t}]' > "$FAKE_DATA/reviews-1.json"
run; [ "$out" = "merge 1 11" ]
check "an owner review older than the verdict is ignored" $?
rm "$FAKE_DATA/reviews-1.json"
comment_inline() { jq -n -c --arg t "$2" '[{user: {login: "Adamniels"}, created_at: $t}]' > "$FAKE_DATA/inline-$1.json"; }
comment_inline 1 "$late"
run; [ "$out" = "fix 1 11" ]
check "an owner inline comment newer than the verdict gives fix" $?
comment_inline 1 "$between"
run; [ "$out" = "merge 1 11" ]
check "an owner inline comment older than the verdict is ignored" $?
setprs 5; verdict 5 approve 0; comment_inline 5 "$late"
run; [ "$out" = "fix 5 15" ]
check "an owner inline comment on a needs-owner PR gives fix" $?

# The slice falls back to the branch name.
pr 6 0; jq -c '.closingIssuesReferences = [] | .headRefName = "12-some-slice"' "$FAKE_DATA/pr-6.json" > "$FAKE_DATA/p" && mv "$FAKE_DATA/p" "$FAKE_DATA/pr-6.json"
setprs 6; verdict 6 approve 0
run; [ "$out" = "merge 6 12" ]
check "without a closing issue the slice is the branch name's leading number" $?

# A PR that closes no issue is skipped with a warning.
setprs 1 2; verdict 1 approve 0; verdict 2 approve 0
jq -c '.[0].closingIssuesReferences = []' "$FAKE_DATA/prs.json" > "$FAKE_DATA/p" && mv "$FAKE_DATA/p" "$FAKE_DATA/prs.json"
run; [ "$code" = 0 ] && [ "$out" = "merge 2 12" ] && grep -q '#1' <<<"$err"
check "a PR without a closing issue is skipped with a warning" $?

# Failures.
setprs 1; verdict 1 approve 0
grep -q -- '--author mac-mini-agent' "$FAKE_GH_LOG"
check "the PR list is limited to the bot's PRs" $?
for bad in "approve sha1 x $vtime" "maybe sha1 0 $vtime" "approve sha1" "" "approve sha1 -1 $vtime"; do
  echo "$bad" > "$FAKE_DATA/verdict-1"
  run; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ] || break
done
check "a garbled verdict line exits 1 and prints nothing" $?
verdict 1 approve 0
echo 11 > "$FAKE_DATA/inflight"; : > "$FAKE_DATA/nextfail"
run; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ]
check "a failing next.sh exits 1 and prints nothing" $?
rm "$FAKE_DATA/nextfail"
run; [ "$(grep -c '^--machine$' "$FAKE_GH_LOG")" = 1 ]
check "the board is read once, by machine" $?
for ep in issues/1/comments pulls/1/comments pulls/1/reviews; do
  FAKE_FAILS_ON="$ep" run; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ] || break
done
check "a failing comments, inline comments or reviews read exits 1" $?
echo '{not json' > "$FAKE_DATA/prs.json"
run; [ "$code" = 1 ] && [ -z "$out" ]
check "unreadable PR JSON exits 1 and prints nothing" $?
setprs 1; verdict 1 approve 0
setprs 1 2; verdict 1 approve 0; echo ERROR > "$FAKE_DATA/verdict-2"
run; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ]
check "a verdict error exits 1 with the error on stderr and prints nothing" $?
FAKE_FAILS=1 run; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ]
check "a failing GitHub read exits 1 and prints nothing" $?

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
