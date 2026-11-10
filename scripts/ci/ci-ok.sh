#!/usr/bin/env bash
# Reads the Actions `needs` context as JSON on stdin. Exits 0 when every job succeeded or was
# skipped, otherwise exits 1 and prints `<job>: <result>` for each job that did neither. A skipped
# job counts as fine because ci.yml skips an area's job when nothing in it changed. Used by the
# `ci-ok` job, the one check branch protection requires.
set -uo pipefail

needs="$(cat)"

# Fail closed: no input at all must not read as "nothing failed".
if [ -z "$needs" ]; then
  echo "ci-ok: could not read the needs context" >&2
  exit 1
fi

bad="$(printf '%s' "$needs" | jq -r 'if type == "object" then . else error("not an object") end | to_entries[] | select(.value.result != "success" and .value.result != "skipped") | "\(.key): \(.value.result)"' 2>/dev/null)" || {
  echo "ci-ok: could not read the needs context" >&2
  exit 1
}

if [ -n "$bad" ]; then
  echo "$bad"
  exit 1
fi
