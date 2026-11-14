#!/usr/bin/env bash
# Reads changed paths on stdin, one per line, and prints which CI areas they touch as seven
# `area=true|false` lines and one `dotnet_services=<JSON array>` line, ready for $GITHUB_OUTPUT.
# With --all it reads nothing and prints every area true and every C# service, for a run with no
# diff to read.
#
# A change under .github/workflows/ sets every area, since a workflow edit can break any of them,
# and so does a change to infra/services.json, so a pull request cannot drop or rename its own
# service to skip its checks. A change under a service's folder sets that service's area and e2e,
# the folders and areas read from the manifest through scripts/agent/services.sh; a change under
# contracts/ sets every area that tests against its fixtures; a change under infra/postgres/ sets
# the core and the brain, whose jobs each run their database setup script; a change under
# libs/python/, or elsewhere in libs/ outside libs/dotnet/, sets the libraries and every Python
# service that may use one; and a change under libs/dotnet/ sets the libraries and e2e. The brain's
# docs set nothing, since no check reads them. The web area is every frontend, the ones
# frontends.sh lists: design/ (except the reference screens, which nothing builds or serves) and a
# folder named web one level under a service, which also sets that service's own area.
#
# dotnet_services lists each C# service, an entry with "language": "dotnet" and area "dotnet",
# whose folder, libs/dotnet/, contracts/, infra/services.json, infra/postgres/ or a workflow
# changed, as the objects services.sh dotnet-services prints, in manifest order; `[]` when none
# did. ci.yml runs .github/workflows/dotnet.yml once for each. Used by the `changes` job in ci.yml.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"

all=false
case "${1:-}" in
  "") ;;
  --all) all=true ;;
  *) echo "usage: changed-areas.sh [--all] < paths" >&2; exit 2 ;;
esac

# "<dir> <area>" per service, and one JSON object per C# service. Read before any output, so a
# manifest that cannot be read fails the job instead of printing areas that read as "nothing
# changed".
if ! services="$(bash "$here/../agent/services.sh" areas)" || [ -z "$services" ] ||
  ! dotnet_entries="$(bash "$here/../agent/services.sh" dotnet-services)"; then
  echo "changed-areas: cannot read the services from infra/services.json" >&2
  exit 1
fi
dotnet_dirs=()
while IFS= read -r entry; do
  [ -n "$entry" ] && dotnet_dirs+=("$(printf '%s' "$entry" | jq -r .dir)")
done <<< "$dotnet_entries"
# One true|false per C# service, in manifest order.
dotnet_set=()
for _ in "${dotnet_dirs[@]+"${dotnet_dirs[@]}"}"; do dotnet_set+=(false); done

core=false canary=false brain=false libs=false scripts=false web=false e2e=false

every_dotnet() {
  local i
  for i in "${!dotnet_set[@]}"; do dotnet_set[i]=true; done
}

# set_area <area>: sets it, or fails for an area this script does not print. A C# service's area,
# dotnet, sets none of the seven: which C# services run is dotnet_services, set below.
set_area() {
  case "$1" in
    core) core=true ;;
    canary) canary=true ;;
    brain) brain=true ;;
    dotnet) ;;
    *) echo "changed-areas: infra/services.json names area $1, which this script does not print" >&2; exit 1 ;;
  esac
}

if [ "$all" = true ]; then
  core=true canary=true brain=true libs=true scripts=true web=true e2e=true
  every_dotnet
else
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
        for i in "${!dotnet_dirs[@]}"; do
          case "$path" in
            "${dotnet_dirs[i]}"/*) dotnet_set[i]=true ;;
          esac
        done
        ;;
    esac
    case "$path" in
      .github/workflows/* | infra/services.json) core=true; canary=true; brain=true; libs=true; scripts=true; web=true; e2e=true; every_dotnet ;;
      # Every service's job with a database runs its setup script on one instance, so any may break.
      infra/postgres/*) core=true; brain=true; e2e=true; every_dotnet ;;
      # The C# library: every C# service uses it, and no Python service can.
      libs/dotnet/*) libs=true; e2e=true; every_dotnet ;;
      libs/*) libs=true; canary=true; brain=true; e2e=true ;;
      contracts/*) core=true; canary=true; brain=true; e2e=true; every_dotnet ;;
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
fi

# The chosen C# services' objects, as one compact array.
chosen="$(
  i=0
  while IFS= read -r entry; do
    [ -z "$entry" ] && continue
    [ "${dotnet_set[i]}" = true ] && printf '%s\n' "$entry"
    i=$((i + 1))
  done <<< "$dotnet_entries"
)"
if ! dotnet_services="$(printf '%s' "$chosen" | jq -cs .)"; then
  echo "changed-areas: cannot write dotnet_services" >&2
  exit 1
fi

echo "core=$core"
echo "canary=$canary"
echo "brain=$brain"
echo "libs=$libs"
echo "scripts=$scripts"
echo "web=$web"
echo "e2e=$e2e"
echo "dotnet_services=$dotnet_services"
