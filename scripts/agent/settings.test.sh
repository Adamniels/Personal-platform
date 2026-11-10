#!/usr/bin/env bash
# Offline tests for the deny rules in .claude/settings.json, the last line of defence for an
# unattended agent: it must never merge, never run the merge gate itself, never push to main,
# never force push and never use an admin bypass.
# Run: bash scripts/agent/settings.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
settings="$here/../../.claude/settings.json"
pass=0
fail=0

ok() { echo "ok   $1"; pass=$((pass + 1)); }
bad() { echo "FAIL $1"; [ -n "${2:-}" ] && echo "     $2"; fail=$((fail + 1)); }

# The whole intended list, one rule per line. A rule missing from settings.json or a rule added
# to it without being added here both fail, so the list cannot drift silently.
expected='Bash(* --admin*)
Bash(*merge-gate.sh*)
Bash(gh pr merge *)
Bash(git push * --force*)
Bash(git push * -f *)
Bash(git push * HEAD:main)
Bash(git push * main)
Bash(git push *:main)
Bash(git push *:refs/heads/main)
Bash(git push --force*)
Bash(git push -f *)'

if ! deny="$(jq -er '.permissions.deny[]' "$settings" 2>&1)"; then
  bad "settings.json parses and has permissions.deny" "$deny"
  echo "$pass passed, $fail failed"
  exit 1
fi
ok "settings.json parses and has permissions.deny"

# has <rule>: one check per rule, so a failure names the rule that is missing.
has() {
  if printf '%s\n' "$deny" | grep -Fxq -- "$1"; then ok "denies $1"; else bad "denies $1" "rule missing"; fi
}
while IFS= read -r rule; do has "$rule"; done <<< "$expected"

got="$(printf '%s\n' "$deny" | LC_ALL=C sort)"
want="$(printf '%s\n' "$expected" | LC_ALL=C sort)"
if [ "$got" = "$want" ]; then
  ok "deny list is exactly the intended list"
else
  bad "deny list is exactly the intended list" "diff (< intended, > settings.json): $(diff <(echo "$want") <(echo "$got") | grep '^[<>]' | tr '\n' ' ')"
fi

# A trailing `:*` is Claude Code's legacy prefix form, so `Bash(git push * main:*)` never matched
# anything; a rule of that shape protects nothing while looking as if it does.
if printf '%s\n' "$deny" | grep -q ':\*)$'; then
  bad "no rule uses the legacy :* prefix form" "$(printf '%s\n' "$deny" | grep ':\*)$' | tr '\n' ' ')"
else
  ok "no rule uses the legacy :* prefix form"
fi

dupes="$(printf '%s\n' "$deny" | sort | uniq -d)"
if [ -z "$dupes" ]; then ok "no rule is listed twice"; else bad "no rule is listed twice" "$dupes"; fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
