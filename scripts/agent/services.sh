#!/usr/bin/env bash
# The one reader of infra/services.json, the service manifest: every service the platform runs, in
# start order. Scripts, CI and the agents' skill read the manifest only through this, so a new
# service is an entry there and no change here. See "The service manifest" in infra/README.md.
#
#   bash scripts/agent/services.sh names            each service's name, one per line
#   bash scripts/agent/services.sh ports            each service's port
#   bash scripts/agent/services.sh health           each health path, for a service that has one
#   bash scripts/agent/services.sh areas            "<dir> <area>" per service, for changed-areas.sh
#   bash scripts/agent/services.sh databases        "<database> <owner role> <app role>" per database
#   bash scripts/agent/services.sh password-vars    every password variable infra/.env must set
#   bash scripts/agent/services.sh dotnet-services  one compact JSON object per C# service
#   bash scripts/agent/services.sh origin           the dev stack's origin, https://platform.test[:port]
#   bash scripts/agent/services.sh run <name> migrate|start
#
# `origin` is https://platform.test with the port Caddy is published on: PLATFORM_HTTPS_PORT in
# infra/.env, or 443, the default, which the origin leaves out. A machine where something else
# holds 443, such as the Mac mini, where Tailscale serve listens on *:443, sets another. See "The
# HTTPS port" in infra/README.md.
#
# A C# service is an entry with "language": "dotnet". `dotnet-services` prints, for each, the
# fields .github/workflows/dotnet.yml takes, for changed-areas.sh: name, dir, postgres (whether it
# has a database), init_script (its `ci.init_script`), migrate, the database's name, owner and app
# roles, and the names of its URL and password variables, each "" when the entry has none.
#
# All in manifest order. `run` runs the entry's command from its folder, in the foreground, with
# bash -c, and exits with the command's code. `migrate` sets the owner's database URL in the
# variable the entry's `owner_url` names, and is a no op that exits 0 for an entry without
# `migrate`. `start` sets the app's URL in `app_url` when the entry has one, never the owner's,
# which it unsets; HOST, 127.0.0.1 unless the caller sets it; PORT, the entry's port; and the
# variable the entry's `public_origin` names, when it has one, to `origin`, unless the caller sets it. Each URL
# is postgres://<role>:<password>@127.0.0.1:5432/<database>, the password read from infra/.env and
# percent encoded, and is set for that one process only, in its environment and never on a command
# line. Nothing here prints a password.
#
# Reads the manifest from the repository it sits in, so the branch's copy reads the branch's
# manifest. Needs jq.
# Exit codes: 0 ok, 1 when the manifest, infra/.env or jq cannot be read, 2 for a usage error or an
# unknown name; `run` otherwise exits with its command's code.
set -uo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd -P)"
manifest="$root/infra/services.json"

die() { echo "services: $1" >&2; exit "${2:-1}"; }
usage() { die "usage: services.sh names|ports|health|areas|databases|password-vars|dotnet-services|origin|run <name> migrate|start" 2; }

command -v jq > /dev/null 2>&1 || die "jq is not installed"

# query <jq filter> [jq args...]: the filter's raw output over the manifest, or exit 1.
query() {
  local filter="$1" out
  shift
  out="$(jq -r "$@" "$filter" "$manifest" 2>&1)" || die "cannot read $manifest: $out"
  [ -n "$out" ] && printf '%s\n' "$out"
  return 0
}

# password <variable>: its value in infra/.env, or exit 1 naming the variable, never the value.
password() {
  local line
  [ -f "$root/infra/.env" ] || die "infra/.env does not exist"
  line="$(grep "^$1=" "$root/infra/.env" | tail -n 1)"
  [ -n "${line#*=}" ] || die "$1 is not set in infra/.env"
  printf '%s' "${line#*=}"
}

# url <role> <password variable> <database>: the connection string, the password percent encoded.
# The password reaches jq through its environment, not its arguments, which ps would show.
url() {
  local encoded
  encoded="$(SERVICES_PW="$(password "$2")" jq -rn 'env.SERVICES_PW | @uri')" || exit 1
  [ -n "$encoded" ] || exit 1
  printf 'postgres://%s:%s@127.0.0.1:5432/%s' "$1" "$encoded" "$3"
}

# origin: https://platform.test, with :<port> unless PLATFORM_HTTPS_PORT is 443 or unset. infra/.env
# is optional here, so a machine without it gets the default; a port that is not one exits 1.
origin() {
  local line port=443
  if [ -f "$root/infra/.env" ]; then
    line="$(grep '^PLATFORM_HTTPS_PORT=' "$root/infra/.env" | tail -n 1)"
    [ -n "${line#*=}" ] && port="${line#*=}"
  fi
  case "$port" in
    '' | 0* | *[!0-9]*) die "PLATFORM_HTTPS_PORT in infra/.env is not a port: $port" ;;
  esac
  [ "${#port}" -le 5 ] && [ "$port" -le 65535 ] || die "PLATFORM_HTTPS_PORT in infra/.env is not a port: $port"
  if [ "$port" = 443 ]; then
    printf 'https://platform.test\n'
  else
    printf 'https://platform.test:%s\n' "$port"
  fi
}

case "${1:-}" in
  names) query '.services[].name' ;;
  origin)
    [ "$#" -eq 1 ] || usage
    origin
    ;;
  ports) query '.services[].port' ;;
  health) query '.services[] | .health // empty' ;;
  areas) query '.services[] | "\(.dir) \(.area)"' ;;
  dotnet-services)
    query '.services[] | select(.language == "dotnet") | {
      name, dir,
      postgres: (.database != null),
      init_script: (.ci.init_script // ""),
      migrate: (.migrate // ""),
      database: (.database.name // ""),
      owner: (.database.owner // ""),
      app: (.database.app // ""),
      owner_url: (.database.owner_url // ""),
      app_url: (.database.app_url // ""),
      owner_password: (.database.owner_password // ""),
      app_password: (.database.app_password // "")
    }' -c
    ;;
  databases) query '.services[] | .database // empty | "\(.name) \(.owner) \(.app)"' ;;
  password-vars) query '.services[] | .database // empty | .owner_password, .app_password' ;;
  run)
    [ "$#" -eq 3 ] || usage
    name="$2" step="$3"
    case "$step" in migrate | start) ;; *) usage ;; esac

    entry="$(query '.services[] | select(.name == $name)' --arg name "$name" -c)" || exit 1
    [ -n "$entry" ] || die "no service named $name in infra/services.json" 2

    field() { printf '%s' "$entry" | jq -r "$1 // empty"; }
    dir="$(field .dir)"
    port="$(field .port)"
    command="$(field ".$step")"
    if [ -z "$command" ]; then
      [ "$step" = migrate ] && exit 0
      die "$name has no $step command"
    fi
    cd "$root/$dir" || die "cannot enter $dir"

    database="$(field .database.name)"
    owner_url="$(field .database.owner_url)"
    if [ "$step" = migrate ]; then
      if [ -n "$database" ]; then
        value="$(url "$(field .database.owner)" "$(field .database.owner_password)" "$database")" || exit 1
        export "$owner_url=$value"
      fi
    else
      # A running service never holds its owner's credentials, even when the caller has them set.
      [ -n "$owner_url" ] && unset "$owner_url"
      app_url="$(field .database.app_url)"
      if [ -n "$app_url" ]; then
        value="$(url "$(field .database.app)" "$(field .database.app_password)" "$database")" || exit 1
        export "$app_url=$value"
      fi
      export HOST="${HOST:-127.0.0.1}" PORT="$port"
      # The origin the browser sends, which the service checks every change against.
      public_origin="$(field .public_origin)"
      if [ -n "$public_origin" ] && [ -z "${!public_origin:-}" ]; then
        value="$(origin)" || exit 1
        export "$public_origin=$value"
      fi
    fi
    unset value
    exec bash -c "$command"
    ;;
  *) usage ;;
esac
