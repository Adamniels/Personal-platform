#!/usr/bin/env bash
# Releases a version of the platform into the hosted copy: it checks the settings, builds the
# commit's images, backs up every database, migrates, starts the new containers and waits for them
# to answer, and records the release. A failed step before the start leaves the previous version
# serving; a failed health check after it rolls back when no migration changed. See "Releasing" in
# infra/hosted/README.md.
#
#   bash scripts/deploy/release.sh [<commit>]
#   bash scripts/deploy/release.sh --local
#
# Run by the owner, by hand, from the root of the real copy's own clone, ~/platform on the Mac
# mini, never from a clone the agents work in. <commit> is origin/main unless given, and must be
# origin/main or an ancestor of it, so an older version can be released again and a branch never
# can. It lives under scripts/ on purpose: what puts code in front of real data changes only
# through a pull request the owner merges.
#
# The project is platform-hosted and the env file infra/hosted/.env, unless HOSTED_PROJECT and
# HOSTED_ENV_FILE say otherwise, as check.sh's do. Every compose command names both, and
# infra/hosted/compose.yaml. The env file is the only source of settings: every variable
# infra/hosted/.env.example names is unset from this script's environment first, so a value
# exported in the shell can neither stand in for a missing one nor override one, and none is ever
# printed or put on a command line.
#
# --local skips fetching and checking out, and builds the tree as it is, changes and all, under the
# env file's PLATFORM_RELEASE, or local, never a commit's tag, so it never retags an image a release
# made. It reads and writes no release record, rolls nothing back and removes no image. It is for
# check.sh and is refused for platform-hosted.
#
# RELEASE_HEALTH_SECONDS, 180 unless set, is how long the health paths have to answer, for the tests.
#
# Exit codes: 0 released; 1 refused by preflight, or a step failed, with what was left running
# printed; 2 wrong usage, before anything runs.
# Written for bash 3.2, the one macOS ships.
set -uo pipefail

usage() {
  echo "usage: bash scripts/deploy/release.sh [<commit>] | --local" >&2
  exit 2
}

local_mode=0
commit_arg=""
for arg in "$@"; do
  case "$arg" in
    --local) local_mode=1 ;;
    -*) usage ;;
    *)
      [ -n "$commit_arg" ] && usage
      commit_arg="$arg"
      ;;
  esac
done
[ "$local_mode" -eq 1 ] && [ -n "$commit_arg" ] && usage

project="${HOSTED_PROJECT:-platform-hosted}"
env_file="${HOSTED_ENV_FILE:-infra/hosted/.env}"
case "$project" in
  "" | *[!a-z0-9_-]* | [!a-z0-9]*)
    echo "release: HOSTED_PROJECT '$project' is not a compose project name" >&2
    exit 2
    ;;
esac
if [ "$local_mode" -eq 1 ] && [ "$project" = platform-hosted ]; then
  echo "release: --local builds the tree as it is, and is refused for platform-hosted, the real copy" >&2
  exit 2
fi
health_seconds="${RELEASE_HEALTH_SECONDS:-180}"
case "$health_seconds" in "" | *[!0-9]*) usage ;; esac

root="$(cd "$(dirname "$0")/../.." && pwd -P)"
cd "$root" || {
  echo "release: cannot enter $root" >&2
  exit 1
}
example=infra/hosted/.env.example
compose_file=infra/hosted/compose.yaml

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# Every name .env.example lists, set or commented out, leaves this environment, so compose reads
# each from the env file alone. PLATFORM_RELEASE is set again below, for every compose command.
all_names="$(sed -nE 's/^(# )?([A-Z_][A-Z0-9_]*)=.*/\2/p' "$example")"
for name in $all_names; do unset "$name"; done
# The ones that must be set: each .env.example sets, and the signing key, which it leaves commented
# out since keygen appends it, and without which the core refuses to start.
required="$(sed -nE 's/^([A-Z_][A-Z0-9_]*)=.*/\1/p' "$example") CORE_SIGNING_KEY"

compose() {
  docker compose -p "$project" --env-file "$env_file" -f "$compose_file" "$@"
}

step() { echo "==> $1"; }
say() { echo "release: $*"; }

# env_value <name>: the value the env file gives, its last line for the name, without surrounding
# quotes. Only ever read into a variable, never printed.
env_value() {
  local value
  value="$(grep -E "^$1=" "$env_file" 2> /dev/null | tail -n 1)"
  value="${value#*=}"
  case "$value" in
    \"*\") value="${value#\"}" value="${value%\"}" ;;
    \'*\') value="${value#\'}" value="${value%\'}" ;;
  esac
  printf '%s' "$value"
}

# orig_ref: the branch, or else the commit, the clone was on, which a release that stops before
# its start puts the clone back on, so the checkout matches what runs.
orig_ref=""
restore_checkout() {
  [ "$local_mode" -eq 1 ] || [ -z "$orig_ref" ] && return 0
  git checkout --quiet "$orig_ref" || say "could not check out $orig_ref again; the clone is on $sha"
}

# stop_before_start <message>: a step before the start failed. The containers that were serving
# still are.
stop_before_start() {
  say "$1"
  say "nothing was started or stopped: the containers that were serving still are"
  restore_checkout
  exit 1
}

# wait_healthy: each health path the manifest lists answers 200 through the proxy's published
# address, within health_seconds in all. A path that does not is printed with what it answered.
wait_healthy() {
  local address base deadline health_path got
  address="$(compose port proxy 80 2> /dev/null | tail -n 1)"
  if [ -z "$address" ]; then
    say "the proxy publishes no port"
    return 1
  fi
  # 0.0.0.0 or [::] when published on every interface; the loopback reaches it either way.
  base="http://127.0.0.1:${address##*:}"
  deadline=$(($(date +%s) + health_seconds))
  for health_path in $(bash scripts/agent/services.sh health); do
    while :; do
      got="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$base$health_path")"
      [ "$got" = 200 ] && break
      if [ "$(date +%s)" -ge "$deadline" ]; then
        say "$health_path answered ${got:-nothing} after $health_seconds seconds"
        return 1
      fi
      sleep 2
    done
    echo "$health_path 200"
  done
}

# --- 1. Preflight. --------------------------------------------------------------------------------

step "1. Preflight"
if ! docker info > /dev/null 2>&1; then
  say "Docker does not answer; start Docker Desktop and run this again"
  exit 1
fi
if [ ! -f "$env_file" ]; then
  say "$env_file does not exist; copy $example to it and fill it in, see infra/hosted/README.md"
  exit 1
fi
missing=""
for name in $required; do
  [ -n "$(env_value "$name")" ] || missing="$missing $name"
done
if [ -n "$missing" ]; then
  say "not set, or empty, in $env_file:$missing"
  exit 1
fi
malformed=""
for name in $(printf '%s\n' $all_names | grep '_PASSWORD$'); do
  value="$(env_value "$name")"
  [ -z "$value" ] && continue
  [[ "$value" =~ ^[A-Za-z0-9]+$ ]] || malformed="$malformed $name"
done
unset value
if [ -n "$malformed" ]; then
  say "each password goes into a database URL as it is, so it must be letters and digits only:$malformed"
  exit 1
fi
case "$(env_value PLATFORM_PUBLIC_ORIGIN)" in
  https://?*) ;;
  *)
    say "PLATFORM_PUBLIC_ORIGIN must begin with https://, the origin a browser sends"
    exit 1
    ;;
esac
if [ "$local_mode" -eq 0 ] && [ -n "$(git status --porcelain)" ]; then
  say "the working tree has changes; a release builds a commit exactly, so commit or remove them first"
  exit 1
fi

# --- 2. Fetch and choose. -------------------------------------------------------------------------

if [ "$local_mode" -eq 1 ]; then
  step "2. Fetch and choose: skipped, --local builds the tree as it is"
  sha=""
  tag="$(env_value PLATFORM_RELEASE)"
  tag="${tag:-local}"
else
  step "2. Fetch and choose"
  orig_ref="$(git symbolic-ref -q --short HEAD)" || orig_ref="$(git rev-parse --verify --quiet 'HEAD^{commit}')"
  if ! git fetch origin; then
    say "git fetch origin failed"
    exit 1
  fi
  target="${commit_arg:-origin/main}"
  if ! sha="$(git rev-parse --verify --quiet "$target^{commit}")" || [ -z "$sha" ]; then
    say "$target is not a commit"
    exit 1
  fi
  if ! git merge-base --is-ancestor "$sha" origin/main; then
    say "$sha is not on origin/main; only main or a commit before it is released"
    exit 1
  fi
  if ! git checkout --quiet --detach "$sha"; then
    say "git checkout of $sha failed"
    restore_checkout
    exit 1
  fi
  tag="$(printf '%s' "$sha" | cut -c 1-12)"
  say "releasing $sha"
fi
export PLATFORM_RELEASE="$tag"

# --- 3. Build. ------------------------------------------------------------------------------------

step "3. Build, as platform-hosted/<service>:$tag"
compose build || stop_before_start "the build failed"

# --- 4. Postgres. ---------------------------------------------------------------------------------

step "4. Postgres, its databases and the backup role"
compose up -d --wait postgres || stop_before_start "Postgres did not start healthy"
# The scripts compose.yaml mounts into the init directory by name, each safe to run again: every
# database a service added since gets made here, and the backup role's CONNECT on it, last.
for script in infra/postgres/initdb/*.sh; do
  [ -f "$script" ] || continue
  compose exec -T postgres bash "/docker-entrypoint-initdb.d/${script##*/}" ||
    stop_before_start "${script##*/} failed inside Postgres"
done
# infra/hosted/postgres/backup-role.sh, mounted under this name so it sorts last.
compose exec -T postgres bash /docker-entrypoint-initdb.d/zz-backup-role.sh ||
  stop_before_start "the backup role's script failed inside Postgres"

# --- 5. Back up. ----------------------------------------------------------------------------------

step "5. Back up"
compose run --rm -T backup now 2>&1 | tee "$tmp/backup.log" || stop_before_start "the backup failed, so nothing was migrated"
backup_name="$(grep -oE '[0-9]{8}T[0-9]{6}Z' "$tmp/backup.log" | head -n 1)"
# How the backup is named in a message: by its time, or, when its output held none, by pointing
# at that output.
backup_shown="${backup_name:-the one whose files are named in the output above}"
backup_name="${backup_name:-<time>}"

# --- 6. Migrate. ----------------------------------------------------------------------------------

step "6. Migrate"
# Each <name>-migrate service, in the compose file's order, one at a time.
migrations="$(sed -nE 's/^  ([a-z0-9_-]+-migrate):[[:space:]]*$/\1/p' "$compose_file")"
[ -n "$migrations" ] || stop_before_start "$compose_file has no <name>-migrate service"
done_migrations=""
for service in $migrations; do
  echo "-- $service"
  if ! compose run --rm -T "$service"; then
    [ -n "$done_migrations" ] && say "these ran before it and are not undone:$done_migrations"
    say "the backup taken before migrating is $backup_shown"
    stop_before_start "$service failed, so no service was restarted"
  fi
  done_migrations="$done_migrations $service"
done

# --- 7. Start, and 8. Health. ---------------------------------------------------------------------

step "7. Start"
started=1
compose up -d || started=0

step "8. Health, through the proxy, up to $health_seconds seconds"
if [ "$started" -eq 1 ] && wait_healthy; then
  healthy=1
else
  healthy=0
  [ "$started" -eq 1 ] || say "docker compose up -d failed"
fi

if [ "$healthy" -eq 0 ]; then
  say "the release did not answer; the containers are left up, for reading their logs:"
  say "  docker compose -p $project --env-file $env_file -f $compose_file logs <service>"
  exit 1
fi

step "9. Running as $tag:"
compose ps --format 'table {{.Service}}\t{{.Image}}\t{{.Status}}'
