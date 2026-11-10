#!/usr/bin/env bash
# Offline tests for verdict.sh. A fake `gh` on PATH prints canned PR comments, one JSON array per page as
# `gh --paginate` does, and logs every call, so no test touches GitHub. Run: bash scripts/agent/verdict.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin" "$tmp/data"

# Prints every file in $FAKE_DATA/page-* in name order; FAKE_FAILS=<n> makes it fail with exit code n.
cat > "$tmp/bin/gh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_GH_LOG"
if [ "${FAKE_FAILS:-0}" != 0 ]; then echo "HTTP 502: Bad Gateway" >&2; exit "$FAKE_FAILS"; fi
case "$*" in
  "api repos/Adamniels/Personal-platform/issues/7/comments --paginate")
    for page in "$FAKE_DATA"/page-*; do [ -f "$page" ] && cat "$page"; done; true ;;
  *) echo "unexpected gh call: $*" >&2; exit 9 ;;
esac
FAKE
chmod +x "$tmp/bin/gh"
export PATH="$tmp/bin:$PATH" FAKE_GH_LOG="$tmp/log" FAKE_DATA="$tmp/data"
# Never reach the real, logged-in gh: if the fake is not first on PATH, stop before any test runs.
[ "$(command -v gh)" = "$tmp/bin/gh" ] || { echo "the fake gh is not first on PATH" >&2; exit 1; }

pass=0 fail=0
check() { # name, condition result
  if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi
}

A=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
B=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
C=0123456789abcdef0123456789abcdef01234567
BOT=mac-mini-agent
id=0

# reset: no comments at all.
reset() { rm -f "$FAKE_DATA"/page-*; id=0; }
# comment <author> <created_at> <body> [page]: appends a comment to page 1 (or the page given).
comment() {
  local page="$FAKE_DATA/page-${4:-1}"
  id=$((id + 1))
  [ -f "$page" ] || echo '[]' > "$page"
  jq -c --arg u "$1" --arg t "$2" --arg b "$3" --argjson id "$id" \
    '. + [{id: $id, user: {login: $u}, created_at: $t, body: $b}]' "$page" > "$page.new" && mv "$page.new" "$page"
}
marker() { printf '<!-- verdict: %s sha=%s -->' "$1" "$2"; }
run() { : > "$FAKE_GH_LOG"; out="$(bash "$here/verdict.sh" "$@" 2>"$tmp/err")"; code=$?; err="$(cat "$tmp/err")"; }

t1=2026-11-01T10:00:00Z t2=2026-11-01T11:00:00Z t3=2026-11-01T12:00:00Z t4=2026-11-01T13:00:00Z

# Usage.
run; [ "$code" = 2 ] && [ -z "$out" ] && [ ! -s "$FAKE_GH_LOG" ]
check "no PR number is a usage error" $?
run abc; [ "$code" = 2 ] && [ ! -s "$FAKE_GH_LOG" ]
check "a non-numeric PR number is a usage error" $?

# No verdict.
reset
run 7; [ "$code" = 0 ] && [ "$out" = "none 0 0 -" ]
check "no comments give none 0 0 -" $?
comment someone "$t1" "looks fine"
run 7; [ "$out" = "none 0 0 -" ]
check "comments without a marker give none" $?

# Newest marker wins.
reset
comment $BOT "$t1" "$(marker approve $A)"
comment $BOT "$t2" "$(marker changes $B)"
run 7; [ "$code" = 0 ] && [ "$out" = "changes $B 1 $t2" ]
check "the newest marker wins (changes after approve)" $?
reset
comment $BOT "$t1" "$(marker changes $A)"
comment $BOT "$t2" "$(marker approve $B)"
run 7; [ "$out" = "approve $B 1 $t2" ]
check "the newest marker wins (approve after changes)" $?
reset
comment $BOT "$t2" "$(marker approve $B)"
comment $BOT "$t1" "$(marker changes $A)"
run 7; [ "$out" = "approve $B 1 $t2" ]
check "newest is by created_at, not by position" $?

# A marker followed by review text on later lines counts.
reset
comment $BOT "$t1" "$(marker approve $C)
## Review

All good."
run 7; [ "$out" = "approve $C 0 $t1" ]
check "a marker on the first line with text below counts" $?

# Malformed markers are ignored; the older valid one stays the verdict.
for bad in \
  "<!-- verdict: approved sha=$A -->" \
  "<!-- verdict: Approve sha=$A -->" \
  "<!-- verdict: approve sha=${A%a} -->" \
  "<!-- verdict: approve sha=${A}a -->" \
  "<!-- verdict: approve sha=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA -->" \
  "<!-- verdict: approve sha=gggggggggggggggggggggggggggggggggggggggg -->" \
  "<!-- verdict: approve  sha=$A -->" \
  "<!-- verdict: approve sha=$A --> trailing" \
  " <!-- verdict: approve sha=$A -->" \
  "<!-- verdict: approve sha=$A -->$(printf '\r')" \
  "<!-- verdict: approve sha=$A" \
  "verdict: approve sha=$A"; do
  reset
  comment $BOT "$t1" "$(marker changes $B)"
  comment $BOT "$t2" "$bad"
  run 7; [ "$out" = "changes $B 1 $t1" ]
  check "a malformed marker is ignored: $bad" $?
done

# A marker on line 2, such as a quoting reply, is ignored.
reset
comment $BOT "$t1" "$(marker changes $B)"
comment $BOT "$t2" "> quoting the review
$(marker approve $A)"
run 7; [ "$out" = "changes $B 1 $t1" ]
check "a marker on line 2 is ignored" $?
reset
comment $BOT "$t1" "
$(marker approve $A)"
run 7; [ "$out" = "none 0 0 -" ]
check "a marker after an empty first line is ignored" $?

# Markers from anyone else are ignored, the owner included.
reset
comment $BOT "$t1" "$(marker changes $B)"
comment Adamniels "$t2" "$(marker approve $A)"
comment someone "$t3" "$(marker approve $A)"
run 7; [ "$out" = "changes $B 1 $t1" ]
check "a marker from another author is ignored" $?
reset
comment Adamniels "$t1" "$(marker changes $A)"
comment someone "$t2" "$(marker changes $A)"
run 7; [ "$out" = "none 0 0 -" ]
check "changes markers from other authors are not counted" $?

# The changes count counts every changes marker, whatever its sha, and only valid ones.
reset
comment $BOT "$t1" "$(marker changes $A)"
comment $BOT "$t2" "$(marker changes $B)"
comment $BOT "$t3" "$(marker changes $A)"
comment $BOT "$t3" "<!-- verdict: changes sha=short -->"
comment $BOT "$t4" "$(marker approve $C)"
run 7; [ "$out" = "approve $C 3 $t4" ]
check "the changes count counts all valid changes markers" $?

# Pages from --paginate are joined.
reset
comment $BOT "$t1" "$(marker changes $A)" 1
comment $BOT "$t2" "$(marker changes $B)" 2
comment someone "$t3" "hello" 2
run 7; [ "$out" = "changes $B 2 $t2" ]
check "comments on every page are read" $?

# The call is the comments endpoint of the PR, paginated.
grep -qx "api repos/Adamniels/Personal-platform/issues/7/comments --paginate" "$FAKE_GH_LOG"
check "reads the PR's comments with --paginate" $?

# A GitHub error exits 1 and prints nothing on stdout.
reset
comment $BOT "$t1" "$(marker approve $A)"
FAKE_FAILS=1 run 7; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ]
check "a GitHub error exits 1 with nothing on stdout" $?
FAKE_FAILS=4 run 7; [ "$code" = 1 ] && [ -z "$out" ] && [ -n "$err" ]
check "any gh failure exit code becomes exit 1" $?
echo 'not json' > "$FAKE_DATA/page-9"
run 7; [ "$code" = 1 ] && [ -z "$out" ]
check "an unreadable response exits 1 with nothing on stdout" $?

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
