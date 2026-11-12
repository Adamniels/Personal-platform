#!/usr/bin/env bash
# Offline tests for changed-areas.sh: paths in on stdin, five area lines out.
# Run: bash scripts/ci/changed-areas.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
pass=0
fail=0

# check <name> <expected: "core canary scripts web e2e" as true/false words> <paths, newline separated>
check() {
  local name="$1" expected="$2" paths="$3" got
  got="$(printf '%s' "$paths" | bash "$here/changed-areas.sh" | tr '\n' ' ' | sed 's/ $//')"
  if [ "$got" = "$expected" ]; then
    echo "ok   $name"; pass=$((pass + 1))
  else
    echo "FAIL $name"; echo "     expected: $expected"; echo "     got:      $got"; fail=$((fail + 1))
  fi
}

check "core source" "core=true canary=false scripts=false web=false e2e=true" "Core/src/lib.rs"
check "docs alone" "core=false canary=false scripts=false web=false e2e=false" "docs/README.md"
check "postgres init" "core=true canary=false scripts=false web=false e2e=true" "infra/postgres/initdb/core.sh"
check "other infra only e2e" "core=false canary=false scripts=false web=false e2e=true" "infra/compose.yaml"
check "canary source" "core=false canary=true scripts=false web=false e2e=true" "canary/app/main.py"
check "scripts" "core=false canary=false scripts=true web=false e2e=false" "scripts/ci/ci-ok.sh"
check "claude settings" "core=false canary=false scripts=true web=false e2e=false" ".claude/settings.json"
check "e2e dir" "core=false canary=false scripts=false web=false e2e=true" "e2e/run.sh"
check "contract fixture" "core=true canary=true scripts=false web=false e2e=true" "contracts/token/token.jwt"
check "workflow sets all" "core=true canary=true scripts=true web=true e2e=true" ".github/workflows/core.yml"
check "mixed paths" "core=true canary=false scripts=true web=false e2e=true" "docs/a.md
Core/Cargo.toml
scripts/x.sh"
check "design package" "core=false canary=false scripts=false web=true e2e=true" "design/ui/src/button/button.tsx"
check "design values" "core=false canary=false scripts=false web=true e2e=true" "design/tokens.css"
check "design reference alone" "core=false canary=false scripts=false web=false e2e=false" "design/reference/home-desktop-light.png"
check "core frontend" "core=true canary=false scripts=false web=true e2e=true" "Core/web/src/main.tsx"
check "canary frontend" "core=false canary=true scripts=false web=true e2e=true" "canary/web/package.json"
check "frontend of a service without an area" "core=false canary=false scripts=false web=true e2e=true" "desk/web/src/app.tsx"
check "web deeper is not a frontend" "core=false canary=true scripts=false web=false e2e=true" "canary/app/web/x.py"
check "empty input" "core=false canary=false scripts=false web=false e2e=false" ""

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
