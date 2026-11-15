#!/usr/bin/env bash
# Offline tests for ci-ok.sh: the Actions `needs` context as JSON on stdin.
# Run: bash scripts/ci/ci-ok.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
pass=0
fail=0

# check <name> <expected exit> <expected output, exact> <needs json>
check() {
  local name="$1" want_rc="$2" want_out="$3" json="$4" out rc
  out="$(printf '%s' "$json" | bash "$here/ci-ok.sh" 2>&1)"; rc=$?
  if [ "$rc" = "$want_rc" ] && [ "$out" = "$want_out" ]; then
    echo "ok   $name"; pass=$((pass + 1))
  else
    echo "FAIL $name"; echo "     expected: rc=$want_rc out=[$want_out]"; echo "     got:      rc=$rc out=[$out]"; fail=$((fail + 1))
  fi
}

check "success and skipped" 0 "" '{"core":{"result":"success"},"canary":{"result":"skipped"}}'
check "cancelled fails" 1 "core: cancelled" '{"core":{"result":"cancelled"}}'
check "failure named, success not" 1 "e2e: failure" '{"e2e":{"result":"failure"},"core":{"result":"success"}}'
check "empty needs" 0 "" '{}'
check "malformed json fails" 1 "ci-ok: could not read the needs context" 'not json'
check "empty stdin fails" 1 "ci-ok: could not read the needs context" ''
check "whitespace only stdin fails" 1 "ci-ok: could not read the needs context" $' \n\t\n'
check "array fails" 1 "ci-ok: could not read the needs context" '[]'
check "string fails" 1 "ci-ok: could not read the needs context" '"x"'
check "a failed hosted job fails" 1 "hosted: failure" '{"core":{"result":"success"},"hosted":{"result":"failure"}}'
check "a skipped hosted job passes" 0 "" '{"core":{"result":"success"},"hosted":{"result":"skipped"}}'

# ci-ok judges only the jobs it needs, so one missing from its needs could fail and still merge.
# Every other job under `jobs:` in ci.yml, read by its indentation, must be in ci-ok's needs.
ci="$here/../../.github/workflows/ci.yml"
jobs="$(awk '/^jobs:/ { in_jobs = 1; next } /^[^ #]/ { in_jobs = 0 } in_jobs && /^  [a-z][a-z0-9_-]*:/ { sub(/:.*/, ""); sub(/^ +/, ""); print }' "$ci" | grep -vx ci-ok | sort)"
needs="$(awk '/^  ci-ok:/ { inside = 1; next } /^  [^ ]/ { inside = 0 } inside && /^    needs:/ { sub(/^    needs: *\[/, ""); sub(/\].*/, ""); gsub(/ /, ""); gsub(/,/, "\n"); print }' "$ci" | sort)"
if [ -n "$jobs" ] && [ "$jobs" = "$needs" ]; then
  echo "ok   ci-ok needs every other job in ci.yml"; pass=$((pass + 1))
else
  echo "FAIL ci-ok needs every other job in ci.yml"
  echo "     jobs:  $(echo $jobs)"; echo "     needs: $(echo $needs)"; fail=$((fail + 1))
fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
