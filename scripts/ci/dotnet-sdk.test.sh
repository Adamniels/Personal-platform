#!/usr/bin/env bash
# Checks that every global.json in the repository pins the same .NET SDK version with
# "rollForward": "latestPatch", so every C# folder builds with the same SDK and a patch release is
# taken while another feature band or major is not. Each C# folder has its own global.json, and
# there is none at the root, since each folder builds from its own directory alone; see "One
# repository" in High-level/docs/platform-architecture.md and libs/dotnet/PlatformAuth/README.md.
# The same checks run on sample trees, so they cannot pass by matching nothing.
# Run: bash scripts/ci/dotnet-sdk.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd -P)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
pass=0
fail=0

ok() { echo "ok   $1"; pass=$((pass + 1)); }
bad() { echo "FAIL $1"; [ -n "${2:-}" ] && echo "     $2"; fail=$((fail + 1)); }

# problems <root>: one line per problem with the global.json files under <root>, nothing when they
# agree. Generated and installed folders are not the repository's own.
problems() {
  local root="$1" file rel versions
  local -a files=()
  while IFS= read -r file; do
    files+=("$file")
  done < <(find "$root" \( -name .git -o -name node_modules -o -name bin -o -name obj \) -prune \
    -o -name global.json -type f -print | sort)

  if [ "${#files[@]}" -eq 0 ]; then
    echo "no global.json"
    return
  fi
  [ -f "$root/global.json" ] && echo "global.json at the root"

  for file in "${files[@]}"; do
    rel="${file#"$root"/}"
    if ! jq -e 'type == "object"' "$file" > /dev/null 2>&1; then
      echo "$rel does not parse"
      continue
    fi
    jq -r --arg f "$rel" '
      (.sdk.version | if type == "string" and test("^[0-9]+\\.[0-9]+\\.[0-9]+$") then empty
        else "\($f): sdk.version is not a version" end),
      (.sdk.rollForward | if . == "latestPatch" then empty
        else "\($f): sdk.rollForward is \(. // "missing"), not latestPatch" end)
    ' "$file"
  done

  versions="$(for file in "${files[@]}"; do jq -r '.sdk.version? // empty' "$file" 2>/dev/null; done | sort -u)"
  [ "$(printf '%s\n' "$versions" | grep -c .)" -gt 1 ] &&
    echo "the SDK versions differ: $(printf '%s' "$versions" | tr '\n' ' ')"
  return 0
}

pin() { # pin <file> <version> <rollForward>
  mkdir -p "$(dirname "$1")"
  printf '{"sdk": {"version": "%s", "rollForward": "%s"}}\n' "$2" "$3" > "$1"
}

# The repository's own files.
got="$(problems "$repo")"
if [ -z "$got" ]; then
  ok "every global.json pins the same SDK with latestPatch"
else
  bad "every global.json pins the same SDK with latestPatch" "$(printf '%s' "$got" | tr '\n' ';')"
fi
if [ -f "$repo/libs/dotnet/PlatformAuth/global.json" ]; then
  ok "the C# library has its own global.json"
else
  bad "the C# library has its own global.json"
fi

# Samples. expect <name> <pattern> <setup command...>, in a fresh tree.
expect() {
  local name="$1" pattern="$2" root="$tmp/$1"
  shift 2
  mkdir -p "$root"
  (cd "$root" && "$@")
  got="$(problems "$root")"
  if printf '%s\n' "$got" | grep -qE "$pattern"; then
    ok "refuses $name"
  else
    bad "refuses $name" "expected /$pattern/, got: $(printf '%s' "$got" | tr '\n' ';')"
  fi
}

agree() {
  pin a/global.json 10.0.404 latestPatch
  pin b/c/global.json 10.0.404 latestPatch
}
got="$(mkdir -p "$tmp/agree" && cd "$tmp/agree" && agree && problems "$tmp/agree")"
if [ -z "$got" ]; then ok "accepts two folders that agree"; else bad "accepts two folders that agree" "$got"; fi

expect "no global.json at all" "no global.json" true
expect "two versions" "versions differ: 10.0.404 10.0.405" \
  eval 'agree; pin b/c/global.json 10.0.405 latestPatch'
expect "another roll forward" "rollForward is latestFeature" \
  eval 'agree; pin a/global.json 10.0.404 latestFeature'
expect "no roll forward" "rollForward is missing" \
  eval 'agree; printf "{\"sdk\": {\"version\": \"10.0.404\"}}" > a/global.json'
expect "no version" "sdk.version is not a version" \
  eval 'agree; printf "{\"sdk\": {\"rollForward\": \"latestPatch\"}}" > a/global.json'
expect "a file that does not parse" "a/global.json does not parse" \
  eval 'agree; printf "{" > a/global.json'
expect "one at the root" "global.json at the root" \
  eval 'agree; pin global.json 10.0.404 latestPatch'
got="$(mkdir -p "$tmp/ignored" && cd "$tmp/ignored" && agree && pin x/node_modules/p/global.json 9.0.100 major && pin y/bin/global.json 9.0.100 major && problems "$tmp/ignored")"
if [ -z "$got" ]; then ok "ignores node_modules, bin and obj"; else bad "ignores node_modules, bin and obj" "$got"; fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
