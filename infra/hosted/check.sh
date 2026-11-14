#!/usr/bin/env bash
# Proves the hosted stack works, end to end, on a throwaway copy: it builds every image, starts the
# compose project hostedcheck with secrets made for this run, runs every migration, puts a test
# HTTPS front before the proxy and a stand in bucket beside the backup job, checks what the cases
# below say through it, runs tests/backup.test.sh and the acceptance suite in e2e/ against it, and
# takes the project down again, volumes included, whatever happened.
# It runs agree.test.sh first. It needs Docker, and for the suite uv and Playwright's Chromium,
# which it never installs; it works while the dev stack is up, since it uses neither its ports nor
# its project.
# See infra/hosted/README.md.
#
# Run from the repository root: bash infra/hosted/check.sh
# HOSTED_PROJECT names the project, hostedcheck unless set. It is never platform-hosted, the
# real copy, and the script refuses that before running any docker command.
#
# Each case prints `ok <name>` or `FAIL <name>`, and the script exits non zero if any failed.
# Written for bash 3.2, the one macOS ships.
set -uo pipefail

project="${HOSTED_PROJECT:-hostedcheck}"
if [ "$project" = platform-hosted ]; then
  echo "check.sh: refusing to run as platform-hosted, the real copy's project" >&2
  exit 2
fi
case "$project" in
  "" | *[!a-z0-9_-]* | [!a-z0-9]*)
    echo "check.sh: HOSTED_PROJECT '$project' is not a compose project name" >&2
    exit 2
    ;;
esac

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd -P)"
tmp="$(mktemp -d)"
env_file="$tmp/env"
results="$tmp/results"
: > "$results"

# The address the test front answers on. --resolve sends platform.test to 127.0.0.1, where the
# front is published, on any machine, whether or not its hosts file names it.
origin="https://platform.test:9443"
resolve="platform.test:9443:127.0.0.1"

ok() { echo "ok $1"; echo "ok $1" >> "$results"; }
bad() {
  echo "FAIL $1"
  [ -n "${2:-}" ] && printf '     %s\n' "$2"
  echo "FAIL $1" >> "$results"
}

# Every compose command names the project and this run's env file, so neither the caller's
# environment nor an infra/hosted/.env written for the real copy reaches it. The front's file adds
# the test HTTPS front, which is not part of the hosted copy.
compose() {
  docker compose -p "$project" --env-file "$env_file" \
    -f "$here/compose.yaml" -f "$here/front/compose.yaml" "$@"
}

# The one thing ever removed: this project's containers, networks and volumes.
teardown() {
  compose --profile migrate down -v --remove-orphans > "$tmp/down.log" 2>&1 ||
    { echo "check.sh: docker compose down failed:" >&2; tail -n 20 "$tmp/down.log" >&2; }
}

# leftovers: prints what of the project still exists, nothing when it is all gone.
leftovers() {
  local label="label=com.docker.compose.project=$project"
  docker ps -a --filter "$label" --format 'container {{.Names}}'
  docker network ls --filter "$label" --format 'network {{.Name}}'
  docker volume ls --filter "$label" --format 'volume {{.Name}}'
}

# --- Secrets for this run only. ------------------------------------------------------------------

# Every variable .env.example lists is unset here, so none set by the caller overrides the env
# file. Each password is made the way .env.example says; the signing key is made by the core's
# own keygen once its image is built, and the backup's recipient by age-keygen in the backup image.
# The bucket is tests/s3_stub.py, which checks no signature, so its key is any random one.
vars="$(sed -nE 's/^(# )?([A-Z_][A-Z0-9_]*)=.*/\2/p' "$here/.env.example")"
for var in $vars; do unset "$var"; done
{
  for var in $(printf '%s\n' "$vars" | grep '_PASSWORD$'); do
    echo "$var=$(openssl rand -hex 24)"
  done
  echo "PLATFORM_PUBLIC_ORIGIN=$origin"
  echo "BACKUP_S3_ENDPOINT=http://s3stub:9000"
  echo "BACKUP_S3_REGION=eu-north-1"
  echo "BACKUP_S3_BUCKET=$project"
  echo "BACKUP_S3_KEY_ID=$(openssl rand -hex 10)"
  echo "BACKUP_S3_KEY=$(openssl rand -hex 20)"
  echo "HOSTED_HTTP_BIND=127.0.0.1:9081"
  # Its own tag, so a check never retags an image the real copy runs.
  echo "PLATFORM_RELEASE=check"
} > "$env_file"

trap 'rm -rf "$tmp"' EXIT
trap 'exit 130' INT TERM

# --- agree.test.sh, first. -----------------------------------------------------------------------

if bash "$here/tests/agree.test.sh" > "$tmp/agree.log" 2>&1; then
  ok agree_test
else
  bad agree_test "$(grep -E '^FAIL|^     ' "$tmp/agree.log" | tr '\n' ' ')"
fi

# --- it_refuses_the_real_project --------------------------------------------------------------

# Run again as the real copy, with a docker on PATH that only records that it was called.
mkdir -p "$tmp/fake"
cat > "$tmp/fake/docker" << 'EOF'
#!/bin/sh
echo "$*" >> "$(dirname "$0")/calls"
EOF
chmod +x "$tmp/fake/docker"
HOSTED_PROJECT=platform-hosted PATH="$tmp/fake:$PATH" bash "$here/check.sh" > "$tmp/refuse.log" 2>&1
code=$?
if [ "$code" -ne 0 ] && [ ! -e "$tmp/fake/calls" ]; then
  ok it_refuses_the_real_project
else
  bad it_refuses_the_real_project "exit $code, docker calls: $(cat "$tmp/fake/calls" 2>/dev/null | tr '\n' ';')"
fi

# --- The stack. ----------------------------------------------------------------------------------

# step <name> <command>...: runs a step of the stack, its output in its own log, and on failure
# prints the log's tail.
step() {
  local name="$1"
  shift
  echo "-- $name"
  if "$@" > "$tmp/$name.log" 2>&1; then return 0; fi
  echo "check.sh: $name failed:" >&2
  tail -n 30 "$tmp/$name.log" >&2
  return 1
}

# code <path>: the status the front answers for a path.
code() {
  curl -k -s -o /dev/null -w '%{http_code}' --max-time 10 --resolve "$resolve" "$origin$1"
}

keygen() {
  local line
  line="$(compose run --rm --no-deps -T core keygen 2> "$tmp/keygen.err")" || return 1
  case "$line" in CORE_SIGNING_KEY=?*) echo "$line" >> "$env_file" ;; *) return 1 ;; esac
}

# recipient: a recipient for the running backup job to encrypt to, whose private key is thrown
# away at once. tests/backup.test.sh makes its own pair for the backups it restores.
recipient() {
  local line
  line="$(compose run --rm --no-deps -T --entrypoint sh backup -c 'age-keygen 2> /dev/null | age-keygen -y')" || return 1
  case "$line" in age1?*) echo "BACKUP_AGE_RECIPIENT=$line" >> "$env_file" ;; *) return 1 ;; esac
}

# backup_role: the backup role's script, run again after the migrations, as a release will, so it
# grants CONNECT on every database that exists, inside the postgres container over its socket.
backup_role() {
  compose exec -T postgres bash /docker-entrypoint-initdb.d/zz-backup-role.sh
}

migrate() {
  local service
  for service in $(compose --profile migrate config --services | grep -- '-migrate$'); do
    echo "-- $service"
    compose run --rm -T "$service" || return 1
  done
}

# wait_healthy: each health path the manifest lists answers 200 through the front, within 180
# seconds in all.
wait_healthy() {
  local deadline=$(($(date +%s) + 180)) health_path
  for health_path in $(bash "$repo/scripts/agent/services.sh" health); do
    until [ "$(code "$health_path")" = 200 ]; do
      if [ "$(date +%s)" -ge "$deadline" ]; then
        echo "$health_path answered $(code "$health_path") after 180 seconds"
        return 1
      fi
      sleep 2
    done
    echo "$health_path 200"
  done
}

# suite: the acceptance suite in e2e/ against the front, its output shown as it runs. Invite codes
# and reset tokens are made in the core's container, through E2E_CORE_COMMAND, see e2e/README.md.
# It installs nothing: uv and Chromium must be there, as the dev gate expects them, and a missing
# one is written to suite.reason. A test marked dev_stack_only is left out.
suite() {
  if ! command -v uv > /dev/null 2>&1; then
    echo "uv is not on PATH, see e2e/README.md" > "$tmp/suite.reason"
    return 1
  fi
  if ! (cd "$repo/e2e" && UV_LOCKED=1 uv run python -c '
import os, sys
from playwright.sync_api import sync_playwright
with sync_playwright() as p:
    sys.exit(0 if os.path.exists(p.chromium.executable_path) else 1)
') > "$tmp/chromium.log" 2>&1; then
    echo "Playwright's Chromium is not installed: from e2e/, uv run playwright install chromium" \
      > "$tmp/suite.reason"
    return 1
  fi
  echo "-- the acceptance suite, against $origin"
  if ! (cd "$repo/e2e" &&
    E2E_CORE_COMMAND="docker compose -p $project --env-file '$env_file' -f infra/hosted/compose.yaml exec -T core platform_core" \
      UV_LOCKED=1 uv run pytest --base-url "$origin" -m "not dev_stack_only"); then
    echo "uv run pytest exited non zero, its output is above" > "$tmp/suite.reason"
    return 1
  fi
}

# The cases that need the stack up. A step that fails ends the run, and every case it did not
# reach fails.
stack() {
  trap teardown EXIT
  trap 'exit 130' INT TERM
  local reached=0 got service id line published ports bad_lines

  # A run killed before its trap ran leaves the project behind; start from nothing.
  teardown

  if step build compose build &&
    step keygen keygen &&
    step recipient recipient &&
    step postgres compose up -d --wait postgres &&
    step migrate migrate &&
    step backup_role backup_role &&
    step up compose up -d &&
    step health wait_healthy; then
    reached=1
  fi
  if [ "$reached" -ne 1 ]; then
    for line in the_stack_builds_and_every_service_answers the_design_files_and_the_gallery_are_served \
      nothing_but_the_proxy_is_published no_container_runs_as_root \
      the_acceptance_suite_passes_against_the_containers backup_test; do
      bad "$line" "not reached, a step above failed"
    done
    compose ps -a > "$tmp/ps.log" 2>&1
    compose logs --no-color --tail 20 > "$tmp/logs.log" 2>&1
    cat "$tmp/ps.log" "$tmp/logs.log" >&2
    return 1
  fi

  # the_stack_builds_and_every_service_answers: the build, the migrations and the health paths
  # are the steps above; canary has no health path, and its API answers 401 without a token.
  got="$(code /canary/api/whoami)"
  if [ "$got" = 401 ]; then
    ok the_stack_builds_and_every_service_answers
  else
    bad the_stack_builds_and_every_service_answers "/canary/api/whoami answered $got, not 401"
  fi

  # the_design_files_and_the_gallery_are_served, from the proxy's image, and nothing else under
  # /design/.
  got="tokens.css $(code /design/tokens.css), gallery $(code /design/gallery/), DESIGN.md $(code /design/DESIGN.md)"
  if [ "$got" = "tokens.css 200, gallery 200, DESIGN.md 404" ]; then
    ok the_design_files_and_the_gallery_are_served
  else
    bad the_design_files_and_the_gallery_are_served "$got"
  fi

  # nothing_but_the_proxy_is_published: of the project's containers only the proxy and the front
  # publish a port, both on 127.0.0.1. A port an image exposes without publishing it has no ->.
  bad_lines=""
  published=""
  while IFS='|' read -r service ports; do
    [ -z "$service" ] && continue
    for line in $(printf '%s' "$ports" | tr -d ' ' | tr ',' '\n' | grep -- '->'); do
      published="$published $service"
      case "$service" in
        proxy | front) case "$line" in 127.0.0.1:*) ;; *) bad_lines="$bad_lines $service:$line" ;; esac ;;
        *) bad_lines="$bad_lines $service:$line" ;;
      esac
    done
  done <<< "$(docker ps --filter "label=com.docker.compose.project=$project" \
    --format '{{.Label "com.docker.compose.service"}}|{{.Ports}}')"
  case "$published" in *proxy*) ;; *) bad_lines="$bad_lines proxy-unpublished" ;; esac
  case "$published" in *front*) ;; *) bad_lines="$bad_lines front-unpublished" ;; esac
  if [ -z "$bad_lines" ]; then
    ok nothing_but_the_proxy_is_published
  else
    bad nothing_but_the_proxy_is_published "$bad_lines"
  fi

  # no_container_runs_as_root: every process of each service of the hosted compose file, the
  # front not among them, runs as a user id other than 0. docker top needs the pid column; a
  # container it lists no process for fails, so the case cannot pass by reading nothing.
  bad_lines=""
  for service in $(docker compose -p "$project" --env-file "$env_file" -f "$here/compose.yaml" config --services); do
    id="$(compose ps -q "$service")"
    if [ -z "$id" ]; then
      bad_lines="$bad_lines $service:not-running"
      continue
    fi
    if ! docker top "$id" -o pid,uid > "$tmp/top.log" 2>&1; then
      bad_lines="$bad_lines $service:$(tr -s ' \n' '_' < "$tmp/top.log")"
      continue
    fi
    got="$(awk 'NR > 1 { print $2 }' "$tmp/top.log")"
    if [ -z "$got" ]; then
      bad_lines="$bad_lines $service:no-process"
      continue
    fi
    for line in $got; do
      [ "$line" = 0 ] && bad_lines="$bad_lines $service:uid-0"
    done
    echo "     $service runs as uid $(echo $got | tr ' ' '\n' | sort -u | tr '\n' ' ')"
  done
  if [ -z "$bad_lines" ]; then
    ok no_container_runs_as_root
  else
    bad no_container_runs_as_root "$bad_lines"
  fi

  # the_acceptance_suite_passes_against_the_containers: every e2e test but those marked
  # dev_stack_only passes against the front, after the cases above, so they have the stack fresh.
  if suite; then
    ok the_acceptance_suite_passes_against_the_containers
  else
    bad the_acceptance_suite_passes_against_the_containers "$(cat "$tmp/suite.reason")"
  fi

  # backup_test: tests/backup.test.sh passes against this project, its output shown as it runs.
  # After the suite, so the databases it dumps and restores hold the suite's accounts and data
  # rather than only what the migrations wrote.
  echo "-- tests/backup.test.sh"
  if HOSTED_PROJECT="$project" HOSTED_ENV_FILE="$env_file" bash "$here/tests/backup.test.sh" |
    tee "$tmp/backup.log"; then
    ok backup_test
  else
    bad backup_test "$(grep -E '^FAIL' "$tmp/backup.log" | tr '\n' ' ')"
  fi
}

( stack )
after_run="$(leftovers)"

# The same teardown when a step fails: start Postgres, fail, and see what is left.
( trap teardown EXIT; compose up -d --wait postgres > "$tmp/failing.log" 2>&1; exit 1 )
after_failure="$(leftovers)"

if [ -z "$after_run" ] && [ -z "$after_failure" ]; then
  ok it_leaves_nothing_behind
else
  bad it_leaves_nothing_behind "after the run: $(echo $after_run); after a failed step: $(echo $after_failure)"
fi

failed="$(grep -c '^FAIL ' "$results")"
echo "$(grep -c '^ok ' "$results") passed, $failed failed"
[ "$failed" -eq 0 ]
