#!/usr/bin/env bash
# Offline tests for frontends.sh, on sample trees.
# Run: bash scripts/ci/frontends.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
pass=0
fail=0
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# tree <name> <paths...>: a sample repository holding empty files at the paths.
tree() {
  local root="$tmp/$1" path
  shift
  mkdir -p "$root"
  for path in "$@"; do
    mkdir -p "$root/$(dirname "$path")"
    : > "$root/$path"
  done
  printf '%s' "$root"
}

# check <name> <expected exit> <expected stdout, exact> <root>
check() {
  local name="$1" want_rc="$2" want="$3" root="$4" got rc
  got="$(bash "$here/frontends.sh" "$root" 2>/dev/null)"; rc=$?
  if [ "$rc" = "$want_rc" ] && [ "$got" = "$want" ]; then
    echo "ok   $name"; pass=$((pass + 1))
  else
    echo "FAIL $name"; echo "     expected: rc=$want_rc [$want]"; echo "     got:      rc=$rc [$got]"; fail=$((fail + 1))
  fi
}

check "only the package" 0 "design/ui" "$(tree one design/ui/package.json Core/Cargo.toml)"
check "package and two service frontends" 0 "design/ui
canary/web
desk/web" "$(tree three design/ui/package.json desk/web/package.json canary/web/package.json)"
check "web without package.json is not a frontend" 0 "design/ui" \
  "$(tree bare design/ui/package.json canary/web/index.html)"
check "web two levels down is not a frontend" 0 "design/ui" \
  "$(tree deep design/ui/package.json canary/app/web/package.json)"
check "none fails" 1 "" "$(tree none Core/Cargo.toml)"

# The repository itself lists the package first.
got="$(bash "$here/frontends.sh" "$here/../.." | head -n 1)"
if [ "$got" = "design/ui" ]; then
  echo "ok   this repository starts with design/ui"; pass=$((pass + 1))
else
  echo "FAIL this repository starts with design/ui, got [$got]"; fail=$((fail + 1))
fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
