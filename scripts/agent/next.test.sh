#!/usr/bin/env bash
# Offline tests for next.sh. A fake `gh` on PATH prints a canned board, as GitHub's GraphQL API would
# before any jq, and logs every call, so no test touches GitHub. Run: bash scripts/agent/next.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# The fake prints every file listed in FAKE_PAGES, one JSON document per page, as `gh --paginate` does.
# FAKE_FAILS=1 makes it fail instead.
cat > "$tmp/gh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_GH_LOG"
if [ "${FAKE_FAILS:-0}" = 1 ]; then echo "HTTP 502: Bad Gateway" >&2; exit 1; fi
for page in $FAKE_PAGES; do cat "$page"; done
FAKE
chmod +x "$tmp/gh"
export PATH="$tmp:$PATH" FAKE_GH_LOG="$tmp/log"
# Never reach the real, logged-in gh: if the fake is not first on PATH, stop before any test runs.
[ "$(command -v gh)" = "$tmp/gh" ] || { echo "the fake gh is not first on PATH" >&2; exit 1; }

# item <number> <status> [state] [parent] [parent's sub-issues, comma separated] [blockers as n:STATE,...] [repo]
item() {
  jq -n -c --argjson n "$1" --arg status "$2" --arg state "${3:-OPEN}" --arg parent "${4:-}" \
    --arg subs "${5:-}" --arg blockers "${6:-}" --arg repo "${7:-Adamniels/Personal-platform}" '
    {fieldValueByName: (if $status == "" then null else {name: $status} end),
     content: {number: $n, title: "Issue \($n)", state: $state, repository: {nameWithOwner: $repo},
       parent: (if $parent == "" then null
                else {number: ($parent | tonumber),
                      subIssues: {nodes: [$subs | split(",")[] | {number: tonumber}]}} end),
       blockedBy: {nodes: [$blockers | split(",")[] | select(. != "") | split(":")
                           | {number: (.[0] | tonumber), state: .[1]}]}}}'
}
# page <file> <item>...: writes one page of the board.
page() {
  local file="$1"; shift
  printf '%s\n' "$@" | jq -s -c '{data: {user: {projectV2: {items: {pageInfo: {hasNextPage: false, endCursor: null}, nodes: .}}}}}' > "$file"
}

pass=0 fail=0
check() { # name, condition result
  if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi
}
run() { : > "$FAKE_GH_LOG"; out="$(bash "$here/next.sh" "$@" 2>&1)"; code=$?; }
section() { # the lines under a heading, up to the next one
  awk -v h="$1:" '$0 == h { on = 1; next } /^[A-Z][a-z ]*:$/ { on = 0 } on' <<<"$out"
}

page "$tmp/order.json" \
  "$(item 30 Ready OPEN 4 26,30,27,29)" \
  "$(item 27 Ready OPEN 4 26,30,27,29)" \
  "$(item 29 Ready OPEN 4 26,30,27,29)" \
  "$(item 26 Ready OPEN 4 26,30,27,29)"
FAKE_PAGES="$tmp/order.json" run
[ "$code" = 0 ] && [ "$(section Proposal)" = "  #26 Issue 26 (under #4)" ] \
  && [ "$(section Alternatives)" = "$(printf '  #30 Issue 30 (under #4)\n  #27 Issue 27 (under #4)')" ]
check "ranks by position among the parent's sub-issues, not by number" $?

page "$tmp/blocked.json" \
  "$(item 26 Ready OPEN 4 26,27,28 '')" \
  "$(item 27 Ready OPEN 4 26,27,28 '26:OPEN,19:CLOSED')" \
  "$(item 28 Ready OPEN 4 26,27,28 '19:CLOSED')"
FAKE_PAGES="$tmp/blocked.json" run
[ "$code" = 0 ] && [ "$(section Proposal)" = "  #26 Issue 26 (under #4)" ] \
  && [ "$(section Alternatives)" = "  #28 Issue 28 (under #4)" ] \
  && [ "$(section Blocked)" = "  #27 Issue 27 (under #4): waits for #26" ]
check "an open blocker blocks, a closed one does not" $?

page "$tmp/statuses.json" \
  "$(item 23 'In progress' OPEN 5 23)" \
  "$(item 31 'In review' OPEN 4 31)" \
  "$(item 32 Backlog OPEN 4 32)" \
  "$(item 33 '' OPEN 4 33)" \
  "$(item 19 Ready CLOSED 5 19)" \
  "$(item 7 Ready OPEN '' '' '' 'Adamniels/fodr-hackathon')"
FAKE_PAGES="$tmp/statuses.json" run
[ "$code" = 0 ] && [ "$(section Proposal)" = "  none" ] && [ "$(section Alternatives)" = "  none" ] \
  && [ "$(section 'In flight')" = "$(printf '  #23 In progress: Issue 23\n  #31 In review: Issue 31')" ]
check "only open Ready issues of this repository are candidates; in flight is listed, never proposed" $?

page "$tmp/orphan.json" \
  "$(item 2 Ready OPEN)" \
  "$(item 30 Ready OPEN 4 30)"
FAKE_PAGES="$tmp/orphan.json" run
[ "$code" = 0 ] && [ "$(section Proposal)" = "  #30 Issue 30 (under #4)" ] && [ "$(section Alternatives)" = "  #2 Issue 2" ]
check "an issue without a parent comes after every one with a parent" $?

page "$tmp/p1.json" "$(item 40 Backlog OPEN 4 40)"
page "$tmp/p2.json" "$(item 41 Ready OPEN 4 41)"
FAKE_PAGES="$tmp/p1.json $tmp/p2.json" run
[ "$code" = 0 ] && [ "$(section Proposal)" = "  #41 Issue 41 (under #4)" ] && grep -q -- '--paginate' "$FAKE_GH_LOG"
check "reads every page of the board" $?

FAKE_FAILS=1 FAKE_PAGES="" run
[ "$code" != 0 ] && ! grep -q '^Proposal:' <<<"$out"
check "a failing read exits non-zero and proposes nothing" $?

FAKE_PAGES="$tmp/order.json" run --number
[ "$code" = 0 ] && [ "$out" = "26" ]
check "--number prints only the proposed slice's number" $?

page "$tmp/allblocked.json" \
  "$(item 27 Ready OPEN 4 26,27 '26:OPEN')" \
  "$(item 26 Blocked OPEN 4 26,27)"
FAKE_PAGES="$tmp/allblocked.json" run --number
[ "$code" = 0 ] && [ -z "$out" ]
check "--number prints nothing when every candidate is blocked" $?

FAKE_PAGES="$tmp/allblocked.json" run
[ "$code" = 0 ] && [ "$(section 'In flight')" = "  #26 Blocked: Issue 26" ]
check "in flight also lists Blocked" $?

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
