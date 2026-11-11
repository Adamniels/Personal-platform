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
# endpoints (default []); FAKE_FAILS_ON=<text> fails any call containing it.
# FAKE_FAILS=1 makes the fake fail.
cat > "$tmp/bin/gh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_GH_LOG"
if [ "${FAKE_FAILS:-0}" = 1 ]; then echo "HTTP 502: Bad Gateway" >&2; exit 1; fi
if [ -n "${FAKE_FAILS_ON:-}" ] && [[ "$*" == *"$FAKE_FAILS_ON"* ]]; then echo "HTTP 502: Bad Gateway" >&2; exit 1; fi
canned() { if [ -f "$FAKE_DATA/$1" ]; then cat "$FAKE_DATA/$1"; else echo '[]'; fi; }
case "$*" in
  "pr list "*) jq -c 'map(del(.commits))' "$FAKE_DATA/prs.json" 2>/dev/null || cat "$FAKE_DATA/prs.json" ;;
  "api repos/Adamniels/Personal-platform/commits/"*)
    sha="${2#repos/Adamniels/Personal-platform/commits/}"
    jq -r --arg s "$sha" '.[] | select(.headRefOid == $s) | .commits[-1].committedDate // ""' "$FAKE_DATA/prs.json" ;;
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
# board.sh get <slice> prints the status in status-<slice> (default "In review"); "boardfail" makes it fail.
cat > "$tmp/scripts/board.sh" <<'FAKE'
#!/usr/bin/env bash
[ "${1:-}" = get ] && [ $# -eq 2 ] || { echo "unexpected board.sh call: $*" >&2; exit 9; }
if [ -f "$FAKE_DATA/boardfail" ]; then echo "HTTP 502: Bad Gateway" >&2; exit 1; fi
if [ -f "$FAKE_DATA/status-$2" ]; then cat "$FAKE_DATA/status-$2"; else echo "In review"; fi
FAKE
chmod +x "$tmp/bin/gh" "$tmp/scripts/verdict.sh" "$tmp/scripts/next.sh" "$tmp/scripts/board.sh"
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
later=2026-11-01T13:00:00Z       # after late
mid=2026-11-01T11:30:00Z         # after the verdict, before late
early=2026-11-01T09:00:00Z       # before both
mid=2026-11-01T11:30:00Z         # after the verdict, before late
later=2026-11-01T13:00:00Z       # after late

# pr <number> <slice> [needs-owner]: writes the fragment for an open bot PR with head sha "sha<number>".
pr() {
  jq -n -c --argjson n "$1" --argjson s "$2" --arg label "${3:-}" --arg d "$committed" \
    '{number: $n, headRefOid: "sha\($n)", headRefName: "branch-\($n)", labels: [$label | select(. != "") | {name: .}],
      closingIssuesReferences: [{number: $s}],
      commits: [{committedDate: "2026-10-30T10:00:00Z"}, {committedDate: $d}]}' > "$FAKE_DATA/pr-$1.json"
}
# setprs <number>...: the PRs gh lists, in the order given.
setprs() {
  rm -f "$FAKE_DATA"/verdict-* "$FAKE_DATA"/comments-* "$FAKE_DATA"/inline-* "$FAKE_DATA"/reviews-* "$FAKE_DATA"/nextfail "$FAKE_DATA"/inflight "$FAKE_DATA"/next \
        "$FAKE_DATA"/status-* "$FAKE_DATA"/boardfail
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

# R51: a PR whose slice is Blocked waits for the owner; owner input newer than its last handling gives fix.
setprs 1 2; verdict 1 changes 1; verdict 2 approve 0; echo Blocked > "$FAKE_DATA/status-11"
run; [ "$code" = 0 ] && [ "$out" = "merge 2 12" ]
check "R51: a Blocked slice's PR with a changes verdict yields nothing, and lower items win" $?
comment 1 Adamniels "$late"
run; [ "$code" = 0 ] && [ "$out" = "fix 1 11" ]
check "R51: owner input newer than the last handling gives fix for a Blocked slice's PR" $?
setprs 1; verdict 1 approve 0; echo Blocked > "$FAKE_DATA/status-11"; echo 16 > "$FAKE_DATA/next"
run; [ "$code" = 0 ] && [ "$out" = "new 16" ]
check "R51: an approved PR of a Blocked slice yields nothing either" $?
setprs 1; verdict 1 changes 1; : > "$FAKE_DATA/boardfail"
run; [ "$code" = 1 ] && [ -z "$out" ]
check "R51: a failing board read exits 1 and prints nothing" $?

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

# R30: a bot reply (an implementer answering without a commit) counts as handling. `reply` writes the
# issue comments of a PR from "<author> <created_at> [marker]" triples, a marker being a valid verdict marker.
sha40=0123456789abcdef0123456789abcdef01234567
pr 5 15 needs-owner
reply() { # reply <pr> <author> <created_at> [marker] ...
  local pr="$1"; shift; local args=()
  while [ $# -ge 2 ]; do
    if [ "${3:-}" = marker ]; then
      args+=("$(jq -n -c --arg u "$1" --arg t "$2" --arg b "<!-- verdict: changes sha=$sha40 -->"$'\n'"text" \
        '{user: {login: $u}, created_at: $t, updated_at: $t, body: $b}')"); shift 3
    else
      args+=("$(jq -n -c --arg u "$1" --arg t "$2" '{user: {login: $u}, created_at: $t, updated_at: $t, body: "Answered every finding."}')"); shift 2
    fi
  done
  printf '%s\n' "${args[@]}" | jq -s -c . > "$FAKE_DATA/comments-$pr.json"
}
setprs 1; verdict 1 changes 1
reply 1 mac-mini-agent "$late"
run; [ "$out" = "review 1 11" ]
check "a changes verdict with a newer bot reply on the same head gives review" $?
verdict 1 changes 4
run; [ "$out" = "cap 1 11" ]
check "a bot reply does not lift the cap" $?
verdict 1 changes 1; reply 1 mac-mini-agent "$between"
run; [ "$out" = "fix 1 11" ]
check "a bot reply older than the changes verdict still gives fix" $?
reply 1 mac-mini-agent "$late" mac-mini-agent "$later" marker
run; [ "$out" = "review 1 11" ]
check "a verdict marker comment is not a reply (the reply before it still counts)" $?
reply 1 mac-mini-agent "$later" marker
run; [ "$out" = "fix 1 11" ]
check "a verdict marker comment by the bot is not a reply" $?
reply 1 someone-else "$late"
run; [ "$out" = "fix 1 11" ]
check "a reply by anyone else is not a bot reply" $?
verdict 1 approve 0; reply 1 Adamniels "$late" mac-mini-agent "$later"
run; [ "$out" = "merge 1 11" ]
check "an owner comment older than a bot reply gives no fix" $?
reply 1 mac-mini-agent "$mid" Adamniels "$late"
run; [ "$out" = "fix 1 11" ]
check "an owner comment newer than the bot reply gives fix" $?
reply 1 Adamniels "$late" mac-mini-agent "$later"
verdict 1 changes 1
run; [ "$out" = "review 1 11" ]
check "an owner comment older than a bot reply on a changes verdict gives review, not fix" $?
setprs 5; verdict 5 approve 0; reply 5 Adamniels "$late" mac-mini-agent "$later"
run; [ "$code" = 0 ] && [ "$out" = "owner 5 15" ]
check "a needs-owner PR whose owner comment is older than the bot reply is not fixed (owner item)" $?
reply 5 mac-mini-agent "$mid" Adamniels "$late"
run; [ "$out" = "fix 5 15" ]
check "a needs-owner PR whose owner comment is newer than the bot reply gives fix" $?
comment_inline() { jq -n -c --arg t "$2" '[{user: {login: "Adamniels"}, created_at: $t}]' > "$FAKE_DATA/inline-$1.json"; }
reply 5 mac-mini-agent "$later"; comment_inline 5 "$late"
run; [ "$out" = "owner 5 15" ]
check "an owner inline comment older than the bot reply gives no fix" $?
setprs 1; verdict 1 approve 0

# needs-owner.
pr 5 15 needs-owner
setprs 5; verdict 5 approve 0
run; [ "$code" = 0 ] && [ "$out" = "owner 5 15" ]
check "R42: a needs-owner PR with an approve verdict gives owner, not merge" $?
verdict 5 changes 1
run; [ "$out" = "fix 5 15" ]
check "R42: a needs-owner PR with a changes verdict (<= 3) gives fix" $?
verdict 5 changes 4
run; [ -z "$out" ]
check "R43b: a needs-owner PR at the round cap yields no line (already handed over)" $?
echo 16 > "$FAKE_DATA/next"
run; [ "$out" = "new 16" ]
check "R43b: lower items win over a handed-over capped PR" $?
rm -f "$FAKE_DATA/next"
verdict 5 changes 4; comment 5 Adamniels "$late"
run; [ "$out" = "fix 5 15" ]
check "R43b: a capped needs-owner PR with newer owner input gives fix" $?
rm -f "$FAKE_DATA/comments-5.json"
pr 8 18; setprs 8; verdict 8 changes 4
run; [ "$out" = "cap 8 18" ]
check "R43b: an unlabelled PR at the round cap gives cap" $?
setprs 5
echo "none 0 0 -" > "$FAKE_DATA/verdict-5"
run; [ "$out" = "review 5 15" ]
check "R42: a needs-owner PR with no verdict gives review" $?
verdict 5 approve 0 oldsha
run; [ "$out" = "review 5 15" ]
check "R42: a needs-owner PR with a verdict for an older commit gives review" $?
verdict 5 approve 0; comment 5 Adamniels "$late"
run; [ "$out" = "fix 5 15" ]
check "a needs-owner PR with a new owner comment gives fix" $?
setprs 5 1; verdict 5 approve 0; verdict 1 approve 0; echo 16 > "$FAKE_DATA/next"
run; [ "$out" = "merge 1 11" ]
check "R42: merge for a plain PR beats owner for a needs-owner PR" $?
setprs 5; verdict 5 approve 0; echo 16 > "$FAKE_DATA/next"
run; [ "$out" = "new 16" ]
check "R42: owner loses to new" $?
echo 17 > "$FAKE_DATA/inflight"
run; [ "$out" = "resume 17" ]
check "R42: owner loses to resume" $?
setprs 1; verdict 1 approve 0
run; [ "$out" = "merge 1 11" ]
check "R42: a plain PR with an approve verdict still gives merge" $?
setprs 5 1; verdict 5 approve 0; verdict 1 changes 1
run; [ "$out" = "fix 1 11" ]
check "R42: fix for a plain PR beats owner" $?

# Resume only for In progress slices without an open PR.
rm -f "$FAKE_DATA/next"; pr 1 11; setprs 1; verdict 1 approve 0; echo 11 > "$FAKE_DATA/inflight"
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
grep 'pr list' "$FAKE_GH_LOG" | grep -qv 'commits' && grep -q 'api repos/Adamniels/Personal-platform/commits/sha1' "$FAKE_GH_LOG"
check "the PR list asks for no commits; the head commit's date is read on its own" $?
FAKE_FAILS_ON="/commits/sha1" run; [ "$code" != 0 ] && [ -z "$out" ]
check "a failing head-commit read exits non-zero and prints nothing" $?
for bad in "approve sha1 x $vtime" "maybe sha1 0 $vtime" "approve sha1" "" "approve sha1 -1 $vtime" \
           "approve sha1 0 yesterday" "approve sha1 0 2026-11-01T11:00:00+01:00" "approve sha1 0 2026-13-45T99:00:00Z"; do
  echo "$bad" > "$FAKE_DATA/verdict-1"
  run; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ]
  check "a garbled verdict line exits 1 and prints nothing: [$bad]" $?
done
verdict 1 approve 0
echo 11 > "$FAKE_DATA/inflight"; : > "$FAKE_DATA/nextfail"
run; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ]
check "a failing next.sh exits 1 and prints nothing" $?
rm "$FAKE_DATA/nextfail"
run; [ "$(grep -c '^--machine$' "$FAKE_GH_LOG")" = 1 ]
check "the board is read once, by machine" $?
for ep in issues/1/comments pulls/1/comments pulls/1/reviews; do
  FAKE_FAILS_ON="$ep" run; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ]
  check "a failing read of $ep exits 1 and prints nothing" $?
done
echo "approve sha1 0 $vtime" > "$FAKE_DATA/verdict-1"
setprs 1; verdict 1 approve 0
jq -c '.[0].commits = []' "$FAKE_DATA/prs.json" > "$FAKE_DATA/p" && mv "$FAKE_DATA/p" "$FAKE_DATA/prs.json"
run; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ]
check "a PR without commits exits 1 and prints nothing" $?
setprs 1; verdict 1 approve 0
jq -c '.[0].commits[-1].committedDate = "garbled"' "$FAKE_DATA/prs.json" > "$FAKE_DATA/p" && mv "$FAKE_DATA/p" "$FAKE_DATA/prs.json"
run; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ]
check "a malformed commit date exits 1 and prints nothing" $?
setprs 1; verdict 1 approve 0
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
