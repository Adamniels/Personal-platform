#!/usr/bin/env bash
# Reads changed paths on stdin, one per line, and prints which CI areas they touch as five
# `area=true|false` lines, ready for $GITHUB_OUTPUT. A change under .github/workflows/ sets every
# area, since a workflow edit can break any of them, and a change under contracts/ sets every area
# that tests against its fixtures. The web area is every frontend, the ones frontends.sh lists:
# design/ (except the reference screens, which nothing builds or serves) and a folder named web one
# level under a service, which also sets that service's own area. Used by the `changes` job in
# ci.yml.
set -uo pipefail

core=false canary=false scripts=false web=false e2e=false

while IFS= read -r path || [ -n "$path" ]; do
  case "$path" in
    .github/workflows/*) core=true; canary=true; scripts=true; web=true; e2e=true ;;
    Core/* | infra/postgres/*) core=true; e2e=true ;;
    canary/*) canary=true; e2e=true ;;
    contracts/*) core=true; canary=true; e2e=true ;;
    scripts/* | .claude/settings.json) scripts=true ;;
    design/reference/*) ;;
    design/*) web=true; e2e=true ;;
  esac
  case "$path" in
    infra/* | e2e/*) e2e=true ;;
  esac
  # A service's frontend: exactly one folder, then web/.
  if [[ "$path" =~ ^[^/]+/web/ ]]; then
    web=true; e2e=true
  fi
done

echo "core=$core"
echo "canary=$canary"
echo "scripts=$scripts"
echo "web=$web"
echo "e2e=$e2e"
