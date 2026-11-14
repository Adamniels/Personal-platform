#!/usr/bin/env bash
# Offline tests for changed-areas.sh: paths in on stdin, seven area lines and dotnet_services out.
# Run: bash scripts/ci/changed-areas.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
pass=0
fail=0

# check <name> <expected: "core canary brain libs scripts web e2e" as true/false words> <paths, newline separated>
# Compares the seven area lines; dotnet_services has checks of its own below.
check() {
  local name="$1" expected="$2" paths="$3" got
  got="$(printf '%s' "$paths" | bash "$here/changed-areas.sh" | grep -v '^dotnet_services=' | tr '\n' ' ' | sed 's/ $//')"
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
  got="$(printf '%s' "$paths" | bash "$tmp/scripts/ci/changed-areas.sh" 2>/dev/null | grep -v '^dotnet_services=' | tr '\n' ' ' | sed 's/ $//')"
  if [ "$got" = "$expected" ]; then
    echo "ok   $name"; pass=$((pass + 1))
  else
    echo "FAIL $name"; echo "     expected: $expected"; echo "     got:      $got"; fail=$((fail + 1))
  fi
}
fake "a manifest's folder sets its area" "core=false canary=true brain=false libs=false scripts=false web=false e2e=true" "Review/src/x.cs"
fake "a nested folder sets its area" "core=true canary=false brain=false libs=false scripts=false web=false e2e=true" "Services/core/x"
fake "a folder not in the manifest sets nothing" "core=false canary=false brain=false libs=false scripts=false web=false e2e=false" "Core/x"

# C# services. dotnet_services is one compact JSON array of the C# services to check, each the
# fields dotnet.yml takes; the checks compare the names in it, in manifest order, and one object
# whole.
names() { # names <changed-areas.sh> <args...>: the names in dotnet_services, space separated
  local script="$1" out json
  shift
  out="$(bash "$script" "$@" 2>&1)" || { echo "exit $?: $out"; return; }
  json="$(printf '%s\n' "$out" | sed -n 's/^dotnet_services=//p')"
  [ -n "$json" ] || { echo "no dotnet_services line"; return; }
  printf '%s' "$json" | jq -r 'map(.name) | join(" ")' 2>/dev/null || echo "not JSON: $json"
}
dotnet() { # dotnet <name> <expected names> <changed-areas.sh> <paths> [args...]
  local name="$1" expected="$2" script="$3" paths="$4" got
  shift 4
  got="$(printf '%s' "$paths" | names "$script" "$@")"
  if [ "$got" = "$expected" ]; then
    echo "ok   $name"; pass=$((pass + 1))
  else
    echo "FAIL $name"; echo "     expected: $expected"; echo "     got:      $got"; fail=$((fail + 1))
  fi
}

real="$here/changed-areas.sh"
dotnet "the C# library sets the manifest's C# service" "review" "$real" "libs/dotnet/PlatformAuth/x"
line="$(printf 'docs/README.md' | bash "$real" | grep '^dotnet_services=')"
if [ "$line" = "dotnet_services=[]" ]; then
  echo "ok   the empty list is written []"; pass=$((pass + 1))
else
  echo "FAIL the empty list is written []"; echo "     got: $line"; fail=$((fail + 1))
fi
check "a C# library sets libs and e2e, not canary or brain" "core=false canary=false brain=false libs=true scripts=false web=false e2e=true" "libs/dotnet/PlatformAuth/src/PlatformAuth/TokenVerifier.cs"
check "a Python library keeps setting libs, canary, brain and e2e" "core=false canary=true brain=true libs=true scripts=false web=false e2e=true" "libs/python/platform-auth/src/platform_auth/verify.py"

cat > "$tmp/infra/services.json" <<'EOF'
{"services": [
  {"name": "core", "dir": "Core", "port": 2, "language": "rust", "area": "core", "start": "true"},
  {"name": "review", "dir": "Review", "port": 3, "language": "dotnet", "area": "dotnet",
   "migrate": "dotnet run --no-restore -- migrate", "start": "dotnet run --no-restore",
   "ci": {"init_script": "infra/postgres/initdb/review.sh"},
   "database": {"name": "review", "owner": "review_owner", "app": "review_app",
     "owner_password": "REVIEW_OWNER_PASSWORD", "app_password": "REVIEW_APP_PASSWORD",
     "owner_url": "REVIEW_OWNER_DATABASE_URL", "app_url": "REVIEW_APP_DATABASE_URL"}},
  {"name": "notes", "dir": "Services/notes", "port": 4, "language": "dotnet", "area": "dotnet", "start": "true"}
]}
EOF
fakescript="$tmp/scripts/ci/changed-areas.sh"
dotnet "a C# library sets every C# service" "review notes" "$fakescript" "libs/dotnet/PlatformAuth/x"
fake "a C# library sets libs and e2e, not canary or brain, beside C# services" "core=false canary=false brain=false libs=true scripts=false web=false e2e=true" "libs/dotnet/PlatformAuth/x"
dotnet "a Python library sets no C# service" "" "$fakescript" "libs/python/platform-auth/x"
fake "a Python library keeps its areas beside C# services" "core=false canary=true brain=true libs=true scripts=false web=false e2e=true" "libs/python/platform-auth/x"
dotnet "a contract fixture sets every C# service" "review notes" "$fakescript" "contracts/token/token.jwt"
dotnet "a C# service's folder sets that service alone" "review" "$fakescript" "Review/src/Program.cs"
dotnet "a nested C# service's folder sets it" "notes" "$fakescript" "Services/notes/x.cs"
fake "a C# service's folder sets e2e and no other area" "core=false canary=false brain=false libs=false scripts=false web=false e2e=true" "Review/src/Program.cs"
dotnet "the manifest sets every C# service" "review notes" "$fakescript" "infra/services.json"
dotnet "a database script sets every C# service" "review notes" "$fakescript" "infra/postgres/initdb/review.sh"
dotnet "a workflow sets every C# service" "review notes" "$fakescript" ".github/workflows/dotnet.yml"
dotnet "docs set no C# service" "" "$fakescript" "docs/README.md"
dotnet "a folder whose name starts like a C# service's is not it" "" "$fakescript" "Reviewer/x"
dotnet "--all sets every C# service" "review notes" "$fakescript" "" --all
fake "--all is read as an argument, not a path" "core=false canary=false brain=false libs=false scripts=false web=false e2e=false" "--all"
all="$(bash "$fakescript" --all < /dev/null | grep -v '^dotnet_services=' | tr '\n' ' ' | sed 's/ $//')"
if [ "$all" = "core=true canary=true brain=true libs=true scripts=true web=true e2e=true" ]; then
  echo "ok   --all sets every area"; pass=$((pass + 1))
else
  echo "FAIL --all sets every area"; echo "     got: $all"; fail=$((fail + 1))
fi
want='{"name":"review","dir":"Review","postgres":true,"init_script":"infra/postgres/initdb/review.sh","migrate":"dotnet run --no-restore -- migrate","database":"review","owner":"review_owner","app":"review_app","owner_url":"REVIEW_OWNER_DATABASE_URL","app_url":"REVIEW_APP_DATABASE_URL","owner_password":"REVIEW_OWNER_PASSWORD","app_password":"REVIEW_APP_PASSWORD"}
{"name":"notes","dir":"Services/notes","postgres":false,"init_script":"","migrate":"","database":"","owner":"","app":"","owner_url":"","app_url":"","owner_password":"","app_password":""}'
got="$(printf 'contracts/x' | bash "$fakescript" | sed -n 's/^dotnet_services=//p' | jq -c '.[]' 2>&1)"
if [ "$got" = "$want" ]; then
  echo "ok   each C# service is the fields dotnet.yml takes"; pass=$((pass + 1))
else
  echo "FAIL each C# service is the fields dotnet.yml takes"; echo "     expected: $want"; echo "     got:      $got"; fail=$((fail + 1))
fi
compact="$(printf 'contracts/x' | bash "$fakescript" | grep -c '^dotnet_services=\[.*\]$')"
if [ "$compact" = 1 ]; then
  echo "ok   dotnet_services is one line"; pass=$((pass + 1))
else
  echo "FAIL dotnet_services is one line"; fail=$((fail + 1))
fi

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
