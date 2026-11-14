#!/usr/bin/env bash
# Reads changed paths on stdin, one per line, and prints which CI areas they touch as seven
# `area=true|false` lines, ready for $GITHUB_OUTPUT. A change under .github/workflows/ sets every
# area, since a workflow edit can break any of them, and so does a change to infra/services.json,
# so a pull request cannot drop or rename its own service to skip its checks. A change under a
# service's folder sets that service's area and e2e, the folders and areas read from the manifest
# through scripts/agent/services.sh; a change under contracts/ sets every area
# that tests against its fixtures; a change under infra/postgres/ sets the core and the brain, whose
# jobs each run their database setup script; and a change under libs/ sets the libraries and every
# service that may use one. The brain's docs set nothing, since no check reads them. The web area
# is every frontend, the ones frontends.sh lists: design/ (except the reference screens, which
# nothing builds or serves) and a folder named web one level under a service, which also sets that
# service's own area. Used by the `changes` job in ci.yml.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"

# "<dir> <area>" per service. Read before any output, so a manifest that cannot be read fails the
# job instead of printing areas that read as "nothing changed".
if ! services="$(bash "$here/../agent/services.sh" areas)" || [ -z "$services" ]; then
  echo "changed-areas: cannot read the services from infra/services.json" >&2
  exit 1
fi

core=false canary=false brain=false libs=false scripts=false web=false e2e=false

# set_area <area>: sets it, or fails for an area this script does not print.
set_area() {
  case "$1" in
    core) core=true ;;
    canary) canary=true ;;
    brain) brain=true ;;
    *) echo "changed-areas: infra/services.json names area $1, which this script does not print" >&2; exit 1 ;;
  esac
}

while IFS= read -r path || [ -n "$path" ]; do
  # A service's own folder. The brain's docs set nothing, since no check reads them.
  case "$path" in
    Brain/docs/*) ;;
    *)
      while read -r dir area; do
        case "$path" in
          "$dir"/*) set_area "$area"; e2e=true ;;
        esac
      done <<< "$services"
      ;;
  esac
  case "$path" in
    .github/workflows/* | infra/services.json) core=true; canary=true; brain=true; libs=true; scripts=true; web=true; e2e=true ;;
    # Both services' jobs run their setup script on one instance, so either may break.
    infra/postgres/*) core=true; brain=true; e2e=true ;;
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
