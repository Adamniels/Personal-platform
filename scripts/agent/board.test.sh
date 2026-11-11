#!/usr/bin/env bash
# Offline tests for board.sh. A fake `gh` on PATH answers canned output (as if --jq had been applied)
# and logs every call, so no test touches GitHub. Run: bash scripts/agent/board.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# FAKE_ON_BOARD: 1 an item with status Backlog, 0 no item, nostatus an item without status, archived only an
# archived item. FAKE_ADD_FAILS=1 makes the add fail; FAKE_RELOOKUP is then what the second lookup finds, and FAKE_RELOOKUP_FAILS=1 makes that lookup fail too.
cat > "$tmp/gh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_GH_LOG"
case "$*" in
  *"projectV2(number"*)
    printf 'PVT_project\tPVTSSF_status\n'
    printf 'opt_backlog\tBacklog\nopt_ready\tReady\nopt_prog\tIn progress\nopt_review\tIn review\nopt_blocked\tBlocked\nopt_done\tDone\n' ;;
  *fieldValueByName*)
    echo "I_issue"
    case "${FAKE_ON_BOARD:-1}" in
      1) printf 'PVTI_item\tBacklog\tfalse\n' ;;
      nostatus) printf 'PVTI_item\t\tfalse\n' ;;
      archived) printf 'PVTI_old\tDone\ttrue\n' ;;
    esac ;;
  *addProjectV2ItemById*)
    if [ "${FAKE_ADD_FAILS:-0}" = 1 ]; then echo "GraphQL: Resource not accessible by integration (addProjectV2ItemById)" >&2; exit 1; fi
    echo "PVTI_new" ;;
  *"issue(number"*)
    if [ "${FAKE_RELOOKUP_FAILS:-0}" = 1 ]; then echo "HTTP 502: Bad Gateway" >&2; exit 1; fi
    if [ -n "${FAKE_RELOOKUP:-}" ]; then echo "$FAKE_RELOOKUP"; fi ;; # gh exits 0 when --jq selects nothing
  *updateProjectV2ItemFieldValue*) echo "PVTI_item" ;;
  *) echo "unexpected gh call: $*" >&2; exit 9 ;;
esac
FAKE
chmod +x "$tmp/gh"
export PATH="$tmp:$PATH" FAKE_GH_LOG="$tmp/log"
# Never reach the real, logged-in gh: if the fake is not first on PATH, stop before any test runs.
[ "$(command -v gh)" = "$tmp/gh" ] || { echo "the fake gh is not first on PATH" >&2; exit 1; }

pass=0 fail=0
check() { # name, condition result
  if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi
}
run() { : > "$FAKE_GH_LOG"; out="$(bash "$here/board.sh" "$@" 2>&1)"; code=$?; }

run status 7 Frozen
[ "$code" = 2 ] && grep -q 'Blocked' <<<"$out" && grep -q 'Backlog' <<<"$out" && grep -q 'Ready' <<<"$out" && grep -q 'In progress' <<<"$out" && grep -q 'In review' <<<"$out" && grep -q 'Done' <<<"$out" && [ ! -s "$FAKE_GH_LOG" ]
check "rejects unknown status" $?

run status abc Ready
[ "$code" = 2 ] && [ ! -s "$FAKE_GH_LOG" ]
check "rejects non-numeric issue" $?

run status 007 Ready
[ "$code" = 2 ] && [ ! -s "$FAKE_GH_LOG" ]
check "rejects an issue number with a leading zero" $?

FAKE_ON_BOARD=1 run status 7 "In review"
[ "$code" = 0 ] && [ "$out" = "#7: In review" ] && grep -q 'updateProjectV2ItemFieldValue' "$FAKE_GH_LOG" && grep 'updateProjectV2ItemFieldValue' "$FAKE_GH_LOG" | grep -q 'opt_review' && ! grep -q addProjectV2ItemById "$FAKE_GH_LOG"
check "sets status by option name" $?

FAKE_ON_BOARD=1 run status 7 Blocked
[ "$code" = 0 ] && [ "$out" = "#7: Blocked" ] && grep 'updateProjectV2ItemFieldValue' "$FAKE_GH_LOG" | grep -q 'opt_blocked'
check "accepts the Blocked status" $?

FAKE_ON_BOARD=0 run status 7 Ready
add_line=$(grep -n addProjectV2ItemById "$FAKE_GH_LOG" | cut -d: -f1)
upd_line=$(grep -n updateProjectV2ItemFieldValue "$FAKE_GH_LOG" | cut -d: -f1)
[ "$code" = 0 ] && [ -n "$add_line" ] && [ -n "$upd_line" ] && [ "$add_line" -lt "$upd_line" ] && grep 'updateProjectV2ItemFieldValue' "$FAKE_GH_LOG" | grep -q 'PVTI_new'
check "adds missing item" $?

FAKE_ON_BOARD=0 FAKE_ADD_FAILS=1 run status 7 Ready
[ "$code" = 1 ] && grep -q 'Resource not accessible by integration' <<<"$out" && ! grep -q updateProjectV2ItemFieldValue "$FAKE_GH_LOG"
check "a failed add prints GitHub's error and exits 1" $?

FAKE_ON_BOARD=0 FAKE_ADD_FAILS=1 FAKE_RELOOKUP_FAILS=1 run status 7 Ready
[ "$code" = 1 ] && grep -q 'Resource not accessible by integration' <<<"$out" && ! grep -q updateProjectV2ItemFieldValue "$FAKE_GH_LOG"
check "a failed add still shows GitHub's error when the second lookup fails too" $?

FAKE_ON_BOARD=0 FAKE_ADD_FAILS=1 FAKE_RELOOKUP=PVTI_race run status 7 Ready
[ "$code" = 0 ] && grep 'updateProjectV2ItemFieldValue' "$FAKE_GH_LOG" | grep -q 'PVTI_race'
check "a failed add whose item exists after all (auto-add race) is updated" $?

FAKE_ON_BOARD=archived run status 7 Ready
[ "$code" = 1 ] && grep -q 'archived' <<<"$out" && grep -q 'includeArchived:true' "$FAKE_GH_LOG" && grep -q isArchived "$FAKE_GH_LOG" && ! grep -q updateProjectV2ItemFieldValue "$FAKE_GH_LOG" && ! grep -q addProjectV2ItemById "$FAKE_GH_LOG"
check "status never touches an archived item" $?

FAKE_ON_BOARD=0 run get 7
[ "$code" = 0 ] && [ "$out" = "none" ]
check "get prints none when not on board" $?

FAKE_ON_BOARD=nostatus run get 7
[ "$code" = 0 ] && [ "$out" = "no status" ]
check "get prints no status for an item without status" $?

FAKE_ON_BOARD=archived run get 7
[ "$code" = 0 ] && [ "$out" = "archived" ]
check "get prints archived for an archived item" $?

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
