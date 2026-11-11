#!/usr/bin/env bash
# Offline tests for changed-areas.sh: paths in on stdin, four area lines out.
# Run: bash scripts/ci/changed-areas.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
pass=0
fail=0

# check <name> <expected: "core canary scripts e2e" as true/false words> <paths, newline separated>
check() {
  local name="$1" expected="$2" paths="$3" got
  got="$(printf '%s' "$paths" | bash "$here/changed-areas.sh" | tr '\n' ' ' | sed 's/ $//')"
  if [ "$got" = "$expected" ]; then
    echo "ok   $name"; pass=$((pass + 1))
  else
    echo "FAIL $name"; echo "     expected: $expected"; echo "     got:      $got"; fail=$((fail + 1))
  fi
}

check "core source" "core=true canary=false scripts=false e2e=true" "Core/src/lib.rs"
check "docs alone" "core=false canary=false scripts=false e2e=false" "docs/README.md"
check "postgres init" "core=true canary=false scripts=false e2e=true" "infra/postgres/initdb/core.sh"
check "other infra only e2e" "core=false canary=false scripts=false e2e=true" "infra/compose.yaml"
check "canary source" "core=false canary=true scripts=false e2e=true" "canary/app/main.py"
check "scripts" "core=false canary=false scripts=true e2e=false" "scripts/ci/ci-ok.sh"
check "claude settings" "core=false canary=false scripts=true e2e=false" ".claude/settings.json"
check "e2e dir" "core=false canary=false scripts=false e2e=true" "e2e/run.sh"
check "workflow sets all" "core=true canary=true scripts=true e2e=true" ".github/workflows/core.yml"
check "mixed paths" "core=true canary=false scripts=true e2e=true" "docs/a.md
Core/Cargo.toml
scripts/x.sh"
check "empty input" "core=false canary=false scripts=false e2e=false" ""

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
