#!/usr/bin/env bash
# Reads changed paths on stdin, one per line, and prints which CI areas they touch as seven
# `area=true|false` lines, ready for $GITHUB_OUTPUT. A change under .github/workflows/ sets every
# area, since a workflow edit can break any of them; a change under contracts/ sets every area
# that tests against its fixtures; and a change under libs/ sets the libraries and every service
# that may use one. The brain's docs set nothing, since no check reads them. The web area is every
# frontend, the ones frontends.sh lists: design/ (except the reference screens, which nothing
# builds or serves) and a folder named web one level under a service, which also sets that
# service's own area. Used by the `changes` job in ci.yml.
set -uo pipefail

core=false canary=false brain=false libs=false scripts=false web=false e2e=false

while IFS= read -r path || [ -n "$path" ]; do
  case "$path" in
    .github/workflows/*) core=true; canary=true; brain=true; libs=true; scripts=true; web=true; e2e=true ;;
    Core/* | infra/postgres/*) core=true; e2e=true ;;
    canary/*) canary=true; e2e=true ;;
    Brain/docs/*) ;;
    Brain/*) brain=true; e2e=true ;;
    libs/*) libs=true; canary=true; brain=true; e2e=true ;;
    contracts/*) core=true; canary=true; brain=true; e2e=true ;;
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
echo "brain=$brain"
echo "libs=$libs"
echo "scripts=$scripts"
echo "web=$web"
echo "e2e=$e2e"
