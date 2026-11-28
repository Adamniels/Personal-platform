#!/usr/bin/env bash
# Reads changed paths on stdin, one per line, and prints the e2e test files the implementer's local
# gate runs for them, one per line relative to e2e/, or the single line `all` for the whole suite.
# Nothing printed means no test reaches the change. CI runs the whole suite on every push; this only
# narrows the local run in scripts/agent/gate.sh, so a slice is not held up by the tests of pages it
# cannot reach. See "The gate" in High-level/docs/agent-workflow.md.
#
#   git diff --name-only <base> | bash scripts/agent/e2e-scope.sh
#
# Each path is first put to scripts/ci/changed-areas.sh: one that does not set e2e there adds nothing,
# so the two never disagree about what reaches e2e. One that does adds:
#   e2e/tests/<file>.py   that file, when it still exists: the slice's own acceptance tests are here
#   a service's folder    the files its manifest entry's "e2e" globs match, and SHARED below; a service
#                         with no "e2e" field adds all, as the core does, since every test signs in
#                         through it
#   anything else         all: e2e's conftest.py and helpers, design/, contracts/, libs/, infra/, a
#                         workflow and the manifest reach every page
# all wins over any list. Needs jq.
# Exit codes: 0 ok, 1 when changed-areas.sh or the manifest cannot be read.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd -P)"
manifest="$root/infra/services.json"

# Every page and every feature's card: test_look.py checks each page of the platform, and
# test_front_page.py renders every feature's front page card, so any service can break them.
SHARED="tests/test_look.py tests/test_front_page.py"

# "<dir> <name>" per service, for the folder match.
services="$(jq -r '.services[] | "\(.dir) \(.name)"' "$manifest")" || { echo "e2e-scope: cannot read $manifest" >&2; exit 1; }

all=0
picked=""
add() { picked="$picked$1"$'\n'; }
# add_globs <service name>: the files its globs match, or all when it has none.
add_globs() {
  local globs glob f
  globs="$(jq -r --arg n "$1" '.services[] | select(.name == $n) | .e2e // empty | .[]' "$manifest")" \
    || { echo "e2e-scope: cannot read $manifest" >&2; exit 1; }
  if [ -z "$globs" ]; then all=1; return; fi
  for glob in $globs $SHARED; do
    for f in "$root"/e2e/$glob; do
      [ -f "$f" ] && add "${f#"$root"/e2e/}"
    done
  done
}

while IFS= read -r path || [ -n "$path" ]; do
  [ -n "$path" ] || continue
  areas="$(printf '%s\n' "$path" | bash "$root/scripts/ci/changed-areas.sh")" \
    || { echo "e2e-scope: changed-areas.sh failed on $path" >&2; exit 1; }
  grep -qx 'e2e=true' <<<"$areas" || continue
  case "$path" in
    e2e/tests/*.py)
      [ -f "$root/$path" ] && add "${path#e2e/}"
      continue
      ;;
  esac
  service=""
  while read -r dir name; do
    case "$path" in "$dir"/*) service="$name" ;; esac
  done <<<"$services"
  if [ -n "$service" ]; then add_globs "$service"; else all=1; fi
done

if [ "$all" = 1 ]; then
  echo all
else
  printf '%s' "$picked" | grep -v '^$' | sort -u
fi
exit 0
