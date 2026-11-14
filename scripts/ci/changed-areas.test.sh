#!/usr/bin/env bash
# Offline tests for changed-areas.sh: paths in on stdin, seven area lines out.
# Run: bash scripts/ci/changed-areas.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
pass=0
fail=0

# check <name> <expected: "core canary brain libs scripts web e2e" as true/false words> <paths, newline separated>
check() {
  local name="$1" expected="$2" paths="$3" got
  got="$(printf '%s' "$paths" | bash "$here/changed-areas.sh" | tr '\n' ' ' | sed 's/ $//')"
  if [ "$got" = "$expected" ]; then
    echo "ok   $name"; pass=$((pass + 1))
  else
    echo "FAIL $name"; echo "     expected: $expected"; echo "     got:      $got"; fail=$((fail + 1))
  fi
}

check "core source" "core=true canary=false brain=false libs=false scripts=false web=false e2e=true" "Core/src/lib.rs"
check "docs alone" "core=false canary=false brain=false libs=false scripts=false web=false e2e=false" "docs/README.md"
check "postgres init" "core=true canary=false brain=true libs=false scripts=false web=false e2e=true" "infra/postgres/initdb/core.sh"
check "brain postgres init" "core=true canary=false brain=true libs=false scripts=false web=false e2e=true" "infra/postgres/initdb/brain.sh"
check "other infra only e2e" "core=false canary=false brain=false libs=false scripts=false web=false e2e=true" "infra/compose.yaml"
check "canary source" "core=false canary=true brain=false libs=false scripts=false web=false e2e=true" "canary/app/main.py"
check "scripts" "core=false canary=false brain=false libs=false scripts=true web=false e2e=false" "scripts/ci/ci-ok.sh"
check "claude settings" "core=false canary=false brain=false libs=false scripts=true web=false e2e=false" ".claude/settings.json"
check "e2e dir" "core=false canary=false brain=false libs=false scripts=false web=false e2e=true" "e2e/run.sh"
check "contract fixture" "core=true canary=true brain=true libs=false scripts=false web=false e2e=true" "contracts/token/token.jwt"
check "workflow sets all" "core=true canary=true brain=true libs=true scripts=true web=true e2e=true" ".github/workflows/core.yml"
check "brain source" "core=false canary=false brain=true libs=false scripts=false web=false e2e=true" "Brain/src/brain/app.py"
check "brain lockfile" "core=false canary=false brain=true libs=false scripts=false web=false e2e=true" "Brain/uv.lock"
check "brain docs alone" "core=false canary=false brain=false libs=false scripts=false web=false e2e=false" "Brain/docs/data-model.md"
check "brain readme" "core=false canary=false brain=true libs=false scripts=false web=false e2e=true" "Brain/README.md"
check "brain schema" "core=true canary=true brain=true libs=false scripts=false web=false e2e=true" "contracts/brain/openapi.json"
check "python library" "core=false canary=true brain=true libs=true scripts=false web=false e2e=true" "libs/python/platform-auth/pyproject.toml"
check "any library" "core=false canary=true brain=true libs=true scripts=false web=false e2e=true" "libs/README.md"
check "lowercase brain is not the brain" "core=false canary=false brain=false libs=false scripts=false web=false e2e=false" "brain/app.py"
check "new workflow sets all" "core=true canary=true brain=true libs=true scripts=true web=true e2e=true" ".github/workflows/libs.yml"
check "mixed paths" "core=true canary=false brain=false libs=false scripts=true web=false e2e=true" "docs/a.md
Core/Cargo.toml
scripts/x.sh"
check "design package" "core=false canary=false brain=false libs=false scripts=false web=true e2e=true" "design/ui/src/button/button.tsx"
check "design values" "core=false canary=false brain=false libs=false scripts=false web=true e2e=true" "design/tokens.css"
check "design reference alone" "core=false canary=false brain=false libs=false scripts=false web=false e2e=false" "design/reference/home-desktop-light.png"
check "core frontend" "core=true canary=false brain=false libs=false scripts=false web=true e2e=true" "Core/web/src/main.tsx"
check "canary frontend" "core=false canary=true brain=false libs=false scripts=false web=true e2e=true" "canary/web/package.json"
check "brain frontend" "core=false canary=false brain=true libs=false scripts=false web=true e2e=true" "Brain/web/src/main.tsx"
check "frontend of a service without an area" "core=false canary=false brain=false libs=false scripts=false web=true e2e=true" "desk/web/src/app.tsx"
check "web deeper is not a frontend" "core=false canary=true brain=false libs=false scripts=false web=false e2e=true" "canary/app/web/x.py"
check "empty input" "core=false canary=false brain=false libs=false scripts=false web=false e2e=false" ""

# The service manifest.
check "the service manifest sets all" "core=true canary=true brain=true libs=true scripts=true web=true e2e=true" "infra/services.json"
check "a core path, from the manifest" "core=true canary=false brain=false libs=false scripts=false web=false e2e=true" "Core/x"
check "a canary path, from the manifest" "core=false canary=true brain=false libs=false scripts=false web=false e2e=true" "canary/x"
check "a brain path, from the manifest" "core=false canary=false brain=true libs=false scripts=false web=false e2e=true" "Brain/x"
check "brain docs still set nothing" "core=false canary=false brain=false libs=false scripts=false web=false e2e=false" "Brain/docs/x"
check "a folder whose name starts like a service's is not it" "core=false canary=false brain=false libs=false scripts=false web=false e2e=false" "Corefoo/x"

# The folders and areas are read from the manifest: a fake repository holding a copy of the script
# and services.sh, with a manifest of its own.
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/scripts/ci" "$tmp/scripts/agent" "$tmp/infra"
cp "$here/changed-areas.sh" "$tmp/scripts/ci/"
cp "$here/../agent/services.sh" "$tmp/scripts/agent/"
cat > "$tmp/infra/services.json" <<'EOF'
{"services": [
  {"name": "review", "dir": "Review", "port": 1, "language": "dotnet", "area": "canary", "start": "true"},
  {"name": "core", "dir": "Services/core", "port": 2, "language": "rust", "area": "core", "start": "true"}
]}
EOF
fake() {
  local name="$1" expected="$2" paths="$3" got
  got="$(printf '%s' "$paths" | bash "$tmp/scripts/ci/changed-areas.sh" 2>/dev/null | tr '\n' ' ' | sed 's/ $//')"
  if [ "$got" = "$expected" ]; then
    echo "ok   $name"; pass=$((pass + 1))
  else
    echo "FAIL $name"; echo "     expected: $expected"; echo "     got:      $got"; fail=$((fail + 1))
  fi
}
fake "a manifest's folder sets its area" "core=false canary=true brain=false libs=false scripts=false web=false e2e=true" "Review/src/x.cs"
fake "a nested folder sets its area" "core=true canary=false brain=false libs=false scripts=false web=false e2e=true" "Services/core/x"
fake "a folder not in the manifest sets nothing" "core=false canary=false brain=false libs=false scripts=false web=false e2e=false" "Core/x"

echo 'not json' > "$tmp/infra/services.json"
printf 'Core/x' | bash "$tmp/scripts/ci/changed-areas.sh" > "$tmp/out" 2>&1
rc=$?
if [ "$rc" -ne 0 ] && ! grep -q '=' "$tmp/out"; then
  echo "ok   fails, printing no area, when the manifest cannot be read"; pass=$((pass + 1))
else
  echo "FAIL fails, printing no area, when the manifest cannot be read"; echo "     rc=$rc: $(cat "$tmp/out")"; fail=$((fail + 1))
fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
