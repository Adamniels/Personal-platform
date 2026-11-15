#!/usr/bin/env bash
# Offline tests for the deny rules in .claude/settings.json, the last line of defence for an
# unattended agent: it must never merge (by gh pr merge however its flags are placed, the REST API, or a GraphQL merge or auto merge), never run the merge gate
# itself, never push to main, never force push (by flag or a + refspec) and never use an admin bypass;
# and in every session, never address the hosted copy, by a docker command naming platform-hosted
# or a command naming its clone, ~/platform. These are patterns over a command's text, not a wall:
# see "Safety" in High-level/docs/agent-workflow.md.
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
Bash(*$HOME/platform*)
Bash(*docker*platform-hosted*)
Bash(*merge-gate.sh*)
Bash(*~/platform*)
Bash(gh api *pulls/*/merge*)
Bash(gh api graphql*enablePullRequestAutoMerge*)
Bash(gh api graphql*mergePullRequest*)
Bash(*gh * pr *merge*)
Bash(*gh pr *merge*)
Bash(gh pr merge *)
Bash(git push *+*)
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

# denied <command>: whether a rule matches the whole command, each `*` in it matching any run of
# characters and everything else matching itself, which is how a Bash rule's pattern is read. It
# models the patterns, not Claude Code: what it says is refused is what the rules' text refuses.
denied() {
  local rule pattern regex
  while IFS= read -r rule; do
    pattern="${rule#Bash(}"
    pattern="${pattern%)}"
    regex="$(printf '%s' "$pattern" | sed -e 's/[][\.^$+?(){}|]/\\&/g' -e 's/\*/.*/g')"
    printf '%s\n' "$1" | grep -Eq "^$regex$" && return 0
  done <<< "$deny"
  return 1
}
refused() { if denied "$1"; then ok "refuses: $1"; else bad "refuses: $1" "no rule matches it"; fi; }
allowed() { if denied "$1"; then bad "allows: $1" "a rule matches it"; else ok "allows: $1"; fi; }

# The hosted copy: its project, a container of it, and its clone, by ~ and by $HOME.
refused 'docker compose -p platform-hosted down'
refused 'docker exec platform-hosted-postgres-1 psql'
refused 'cat ~/platform/infra/hosted/.env'
refused 'ls $HOME/platform'
# What the agents do run: check.sh, its own project, and a commit or a search that names the
# project without docker before it.
allowed 'bash infra/hosted/check.sh'
allowed 'docker compose -p hostedcheck ps'
allowed 'git commit -m "Name the platform-hosted project in the runbook"'
# The matcher itself, so the cases above cannot pass by matching nothing or everything.
refused 'gh pr merge 7'
allowed 'gh pr view 7'

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
