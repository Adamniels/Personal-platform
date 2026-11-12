#!/usr/bin/env bash
# Prints every frontend's folder, one per line, relative to the repository root: design/ui, the
# component package, and each folder named web one level under a service that holds a
# package.json, such as canary/web. Each runs the same gate and has a `build` script, so CI
# (web.yml, e2e.yml) and the agents loop over this list and need no change when a frontend is
# added. See design/ui/README.md. Fails when it finds none, since design/ui always exists.
# Run: bash scripts/ci/frontends.sh [repository root, default .]
set -uo pipefail

cd "${1:-.}" || exit 1

found=0
for dir in design/ui */web; do
  if [ -f "$dir/package.json" ]; then
    printf '%s\n' "$dir"
    found=1
  fi
done

if [ "$found" -eq 0 ]; then
  echo "frontends: no frontend found, not even design/ui" >&2
  exit 1
fi
