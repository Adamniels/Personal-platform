#!/usr/bin/env bash
# The implementer's local gate: what CI runs for this branch, run on the Mac mini before each task's commit
# and before the pull request, so ci-ok passes on the first push. /pick-up-slice calls it in step 9; the
# skill no longer spells the steps out, so every run gates the same way.
#
#   bash scripts/agent/gate.sh [--no-cache] [--keep-running]
#   bash scripts/agent/gate.sh --start-platform     starts the platform as e2e does, and leaves it running
#   bash scripts/agent/gate.sh --stop-platform      stops what this clone's services listen on
#
# The two platform modes run no area and use no cache. They are for what needs the platform outside the gate,
# such as the before screenshots of /pick-up-slice step 6: start it, run the tests that cover the page, stop it.
# --start-platform prints the log directory and `platform: up at <origin>`, or each service's log tail and
# exits 1 when it never answers.
#
# Run from anywhere in the clone, on the slice's branch, after the preflight of /pick-up-slice (step 4): the
# dev stack's Postgres up, infra/.env and Core/.env in place, the database reset and migrated.
#
# What runs. The areas come from scripts/ci/changed-areas.sh over the branch's changes: every path that
# differs between its merge base with origin/main and the working tree, untracked files included, so a
# task is gated before it is committed. For each area set, the steps of its workflow under .github/workflows/
# run here in the same order, and gate.test.sh fails when a workflow runs a check this script does not:
#   core     core.yml, from Core/, with the core's migrations from services.sh and sqlx-cli at the version
#            core.yml installs (a machine with another version fails, naming the install command)
#   canary   canary.yml, from canary/
#   brain    brain.yml, from Brain/, with the brain's migrations from services.sh
#   libs     libs.yml, every Python and C# library
#   dotnet-<name>  dotnet.yml, for each C# service changed-areas.sh chose, from its folder
#   scripts  ci.yml's script tests, every scripts/**/*.test.sh
#   web      web.yml, every frontend frontends.sh lists
#   e2e      e2e.yml against the dev stack, scoped (below)
# hosted never runs here: CI's hosted job runs infra/hosted/check.sh whenever hosted=true, and a failure there
# comes back to the implementer as a CI fix round. Every area runs even after one fails, as CI's jobs do; a
# failing step ends its own area.
#
# e2e runs the files scripts/agent/e2e-scope.sh picks for the same paths, or the whole suite when it prints
# `all`; CI runs the whole suite on every push. First the platform is started from the working tree: any
# service still listening from this clone is stopped, every frontend is built (unless web passed in this
# call), Caddy is recreated so it reads the branch's Caddyfile, every service is migrated and started in the
# background with its log in the log directory, and every health path must answer 200 through Caddy within
# three minutes. If one does not, e2e fails at once with each service's log tail: the suite never runs against
# a stack that is not up. e2e/screenshots/ is emptied first, so the PNGs the suite writes are this run's. The
# services are stopped when the gate ends, unless --keep-running.
#
# The cache. An area that passed is recorded by a key: a hash of the area's name, this script, the manifest,
# the scripts that pick the areas, and the content, as it is in the working tree, of every file that can reach
# the area (its paths in `paths_of` below; the scripts and e2e areas take the whole tree). An area whose key is
# recorded prints `cached` and does not run, so a resume or a fix round runs only what its change can reach.
# --no-cache runs every area. Ignored files, such as infra/.env, built output and dependencies, are not in the
# key.
#
# Output: the log directory, the areas, then one line per area, `ok <area>`, `cached <area>` or
# `FAIL <area> (log: <file>)` followed by the log's last 40 lines, then `skip hosted (...)` when hosted is set,
# and last `gate: passed` or `gate: failed`. No password is printed or logged: database URLs are exported, and
# each step's command line is logged before it runs.
#
# Environment (defaults in brackets):
#   AGENT_GATE_CACHE          [$HOME/agent-logs/gate-cache] the recorded keys, one per line
#   AGENT_GATE_LOGS           [a new temp directory] where each area's and each service's log goes
#   AGENT_GATE_HEALTH_TRIES   [90] health rounds, 2 s apart: a test override
#   AGENT_SLEEP               [sleep] the sleep command: a test override
# Written for bash 3.2, the one macOS ships. Needs git, jq and curl, and the tools each area uses.
# Exit codes: 0 every area passed or was cached, 1 an area failed or the branch could not be read, 2 usage.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd -P)"
cd "$root" || exit 1

no_cache=0 keep=0 mode=gate
usage() { echo "usage: gate.sh [--no-cache] [--keep-running] | --start-platform | --stop-platform" >&2; exit 2; }
for arg in "$@"; do
  case "$arg" in
    --no-cache) no_cache=1 ;;
    --keep-running) keep=1 ;;
    --start-platform) mode=start ;;
    --stop-platform) mode=stop ;;
    *) usage ;;
  esac
done
[ "$mode" = gate ] || [ "$#" = 1 ] || usage

cache="${AGENT_GATE_CACHE:-$HOME/agent-logs/gate-cache}"
logs="${AGENT_GATE_LOGS:-$(mktemp -d)}"
health_tries="${AGENT_GATE_HEALTH_TRIES:-90}"
sleep_cmd="${AGENT_SLEEP:-sleep}"
mkdir -p "$logs" "$(dirname "$cache")" || { echo "gate: cannot create $logs or the cache's folder" >&2; exit 1; }
export UV_LOCKED=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

die() { echo "gate: $1" >&2; exit 1; }

# --- What changed ------------------------------------------------------------------------------------------------
# Read for the gate only; the platform modes skip it, below the functions.
if [ "$mode" = gate ]; then
base="$(git merge-base origin/main HEAD 2> /dev/null)" || die "no merge base with origin/main; run git fetch origin"
paths="$( { git diff --name-only "$base" && git ls-files --others --exclude-standard; } | sort -u)" \
  || die "cannot list the changed paths"
areas="$(printf '%s\n' "$paths" | sed '/^$/d' | bash scripts/ci/changed-areas.sh)" || die "changed-areas.sh failed"
area() { grep "^$1=" <<<"$areas" | cut -d= -f2-; }
scope=""
if [ "$(area e2e)" = true ]; then
  scope="$(printf '%s\n' "$paths" | sed '/^$/d' | bash scripts/agent/e2e-scope.sh | tr '\n' ' ' | sed 's/ $//')" \
    || die "e2e-scope.sh failed"
fi

# The working tree as an index, untracked files included, so the keys hash what is on disk, not what is committed.
index="$logs/.index"
cp "$(git rev-parse --git-path index)" "$index" 2> /dev/null || rm -f "$index"
GIT_INDEX_FILE="$index" git add -A > /dev/null 2>&1 || die "cannot read the working tree"
fi

# paths_of <area>: what can reach it, beyond the scripts every key holds.
paths_of() {
  case "$1" in
    core) echo "Core contracts infra/postgres .github/workflows/core.yml" ;;
    canary) echo "canary contracts libs .github/workflows/canary.yml" ;;
    brain) echo "Brain contracts infra/postgres libs .github/workflows/brain.yml" ;;
    libs) echo "libs .github/workflows/libs.yml .github/workflows/dotnet.yml" ;;
    dotnet-*) echo "$2 libs/dotnet contracts infra/postgres .github/workflows/dotnet.yml" ;;
    web) echo "design $(bash scripts/ci/frontends.sh | tr '\n' ' ') .github/workflows/web.yml scripts/ci/frontends.sh" ;;
    *) echo "." ;;
  esac
}
# key <name> <extra> <area>: the cache key.
key() {
  # shellcheck disable=SC2046
  { echo "$1 $2"
    GIT_INDEX_FILE="$index" git ls-files -s -- $(paths_of "$3" "$2") scripts/agent/gate.sh scripts/agent/services.sh \
      scripts/agent/e2e-scope.sh scripts/ci/changed-areas.sh infra/services.json
  } | git hash-object --stdin
}

# --- Running an area -------------------------------------------------------------------------------------------
failed=0 passed=" "
# run <command...>: logs the command line, then runs it. Never pass a secret as an argument: export it.
run() { echo "== $* (in ${PWD#"$root"/})"; "$@"; }
# run_area <name> <key> <function> [args...]: runs the function in a subshell under set -e, its output in the
# area's log, and records the key when it passes.
run_area() {
  local name="$1" k="$2" log="$logs/$1.log" rc
  shift 2
  if [ "$no_cache" = 0 ] && grep -qxF "$k" "$cache" 2> /dev/null; then
    echo "cached $name"; passed="$passed$name "; return 0
  fi
  ( set -e; "$@" ) > "$log" 2>&1; rc=$?
  if [ "$rc" = 0 ]; then
    echo "ok $name"; passed="$passed$name "; echo "$k" >> "$cache"
  else
    echo "FAIL $name (log: $log)"; tail -n 40 "$log" | sed 's/^/     /'; failed=1
  fi
}

# env_value <variable>: its value in infra/.env, never printed by the caller.
env_value() { grep "^$1=" infra/.env 2> /dev/null | tail -n 1 | cut -d= -f2-; }
# url <role> <password variable> <database>: a connection string, the password percent encoded as services.sh does.
url() {
  local encoded
  encoded="$(GATE_PW="$(env_value "$2")" jq -rn 'env.GATE_PW | @uri')"
  [ -n "$encoded" ] || { echo "$2 is not set in infra/.env" >&2; return 1; }
  printf 'postgres://%s:%s@127.0.0.1:5432/%s' "$1" "$encoded" "$3"
}
admin_url() { url postgres POSTGRES_PASSWORD postgres; }

core_area() {
  local want have
  want="$(sed -n -E 's/.*cargo install sqlx-cli --version ([0-9.]+).*/\1/p' .github/workflows/core.yml)"
  cd Core
  run cargo fmt --check
  (cd "$root" && run bash scripts/agent/services.sh run core migrate)
  run cargo clippy --locked --all-targets -- -D warnings
  (export CORE_TEST_ADMIN_DATABASE_URL="$(admin_url)"; run cargo test --locked)
  (export CORE_OWNER_DATABASE_URL="$(url core_owner CORE_OWNER_PASSWORD core)"; run cargo test --locked --test app_roles -- --ignored)
  have="$(cargo sqlx --version 2> /dev/null | awk '{ print $NF }')"
  if [ "$have" != "$want" ]; then
    echo "sqlx-cli is '${have:-missing}', core.yml installs $want:"
    echo "cargo install sqlx-cli --version $want --locked --no-default-features --features postgres,sqlx-toml"
    return 1
  fi
  run cargo sqlx prepare --check -- --all-targets --locked
}

canary_area() {
  cd canary
  run uv run ruff check
  run uv run ruff format --check
  run uv run pytest
}

brain_area() {
  cd Brain
  run uv run ruff check
  run uv run ruff format --check
  run uv run mypy
  (cd "$root" && run bash scripts/agent/services.sh run brain migrate)
  export BRAIN_TEST_ADMIN_DATABASE_URL BRAIN_APP_DATABASE_URL
  BRAIN_TEST_ADMIN_DATABASE_URL="$(admin_url)"
  BRAIN_APP_DATABASE_URL="$(url brain_app BRAIN_APP_PASSWORD brain)"
  run uv run pytest
}

libs_area() {
  local dir status=0
  for dir in libs/python/*/; do
    [ -f "$dir/pyproject.toml" ] || continue
    (
      set -e; cd "$dir"
      run uv run ruff check
      run uv run ruff format --check
      run uv run mypy
      run uv run pytest
    ) || status=1
  done
  for dir in libs/dotnet/*/; do
    ls "$dir"*.slnx > /dev/null 2>&1 || continue
    (
      set -e; cd "$dir"
      if [ -f .config/dotnet-tools.json ]; then run dotnet tool restore; fi
      run dotnet restore --locked-mode
      run dotnet format --verify-no-changes --no-restore
      run dotnet build --no-restore -c Release
      run dotnet test --no-build --no-restore -c Release
    ) || status=1
  done
  return "$status"
}

# dotnet_area <one object of changed-areas.sh's dotnet_services>
dotnet_area() {
  local json="$1" name dir admin app_var
  field() { jq -r ".$1" <<<"$json"; }
  name="$(field name)" dir="$(field dir)"
  cd "$dir"
  if [ -f .config/dotnet-tools.json ]; then run dotnet tool restore; fi
  run dotnet restore --locked-mode
  run dotnet format --verify-no-changes --no-restore
  run dotnet build --no-restore -c Release
  if [ "$(field postgres)" = true ]; then
    (cd "$root" && run bash scripts/agent/services.sh run "$name" migrate)
    admin="$(printf '%s' "$name" | tr '[:lower:]-' '[:upper:]_')_TEST_ADMIN_DATABASE_URL"
    export "$admin=$(admin_url)"
    app_var="$(field app_url)"
    if [ -n "$app_var" ]; then
      export "$app_var=$(url "$(field app)" "$(field app_password)" "$(field database)")"
    fi
  fi
  run dotnet test --no-build --no-restore -c Release
}

scripts_area() {
  local t status=0
  for t in $(find scripts -name '*.test.sh' | sort); do
    run bash "$t" || status=1
  done
  return "$status"
}

web_area() {
  local dir status=0
  for dir in $(bash scripts/ci/frontends.sh); do
    (
      set -e; cd "$dir"
      run npm ci
      run npx biome ci .
      run npx tsc --noEmit
      run npx vitest run
      run npm run build
    ) || status=1
  done
  return "$status"
}

# --- The platform, for e2e ---------------------------------------------------------------------------------------
# stop_services: stops what listens on a service's port from this clone, by its command or its working folder,
# and waits for the port to free. Anything else on the port is an error naming it.
stop_services() {
  local port pid cmd cwd
  for port in $(bash scripts/agent/services.sh ports); do
    for pid in $(lsof -ti "tcp:$port" -sTCP:LISTEN 2> /dev/null); do
      cmd="$(ps -o command= -p "$pid" 2> /dev/null)"
      cwd="$(lsof -a -p "$pid" -d cwd -Fn 2> /dev/null | sed -n 's/^n//p')"
      case "$cmd $cwd/" in
        *"$root/"*) kill "$pid" 2> /dev/null ;;
        *) echo "port $port is held by something outside this clone: $cmd"; return 1 ;;
      esac
    done
    for _ in 1 2 3 4 5 6 7 8 9 10; do
      [ -z "$(lsof -ti "tcp:$port" -sTCP:LISTEN 2> /dev/null)" ] && break
      "$sleep_cmd" 1
    done
  done
}

start_platform() {
  local dir name health_path up origin
  stop_services
  case "$passed" in
    *" web "*) echo "== the frontends were built by the web area" ;;
    *) for dir in $(bash scripts/ci/frontends.sh); do (set -e; cd "$dir"; run npm ci; run npm run build); done ;;
  esac
  (cd infra && run docker compose up -d --wait --force-recreate caddy)
  (cd infra && run docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt "$logs/caddy-root.crt")
  for name in $(bash scripts/agent/services.sh names); do
    run bash scripts/agent/services.sh run "$name" migrate
  done
  : > "$logs/.started"
  for name in $(bash scripts/agent/services.sh names); do
    echo "== start $name, log $logs/service-$name.log"
    nohup bash scripts/agent/services.sh run "$name" start > "$logs/service-$name.log" 2>&1 &
  done
  origin="$(bash scripts/agent/services.sh origin)"
  up=false
  for _ in $(seq 1 "$health_tries"); do
    up=true
    for health_path in $(bash scripts/agent/services.sh health); do
      [ "$(curl --cacert "$logs/caddy-root.crt" -s -o /dev/null -w '%{http_code}' "$origin$health_path")" = 200 ] \
        || { up=false; break; }
    done
    [ "$up" = true ] && break
    "$sleep_cmd" 2
  done
  if [ "$up" != true ]; then
    echo "the platform never answered 200 on every health path at $origin; each service's log ends:"
    for name in $(bash scripts/agent/services.sh names); do
      echo "--- $name"; tail -n 15 "$logs/service-$name.log" 2> /dev/null
    done
    return 1
  fi
}

# e2e_area <scope>: the scope e2e-scope.sh printed, `all`, or empty when no test reaches the change.
e2e_area() {
  local origin
  start_platform
  origin="$(bash scripts/agent/services.sh origin)"
  cd e2e
  rm -f screenshots/*.png
  run uv run playwright install chromium
  case "$1" in
    all) run uv run pytest --base-url "$origin" ;;
    "") echo "== no e2e test reaches this change" ;;
    # shellcheck disable=SC2086
    *) run uv run pytest --base-url "$origin" $1 ;;
  esac
  run uv run ruff check
  run uv run ruff format --check
}

finish() { [ "$keep" = 1 ] || [ ! -f "$logs/.started" ] || stop_services > /dev/null 2>&1; rm -f "$logs/.started" "$index"; }
trap finish EXIT

case "$mode" in
  start)
    keep=1
    echo "platform: logs in $logs"
    if start_platform > "$logs/platform.log" 2>&1; then
      echo "platform: up at $(bash scripts/agent/services.sh origin)"; exit 0
    fi
    tail -n 60 "$logs/platform.log"; exit 1
    ;;
  stop) stop_services; exit $? ;;
esac

# --- The gate --------------------------------------------------------------------------------------------------
echo "gate: logs in $logs"
echo "gate: $(grep '=true$' <<<"$areas" | cut -d= -f1 | tr '\n' ' ')$(grep -q '^dotnet_services=\[\]$' <<<"$areas" || echo "and C# $(area dotnet_services | jq -r '[.[].name] | join(" ")')")"
[ -z "$scope" ] || echo "gate: e2e runs $scope"

for a in core canary brain libs; do
  [ "$(area "$a")" = true ] && run_area "$a" "$(key "$a" "" "$a")" "${a}_area"
done
while IFS= read -r svc; do
  [ -n "$svc" ] || continue
  name="$(jq -r .name <<<"$svc")" dir="$(jq -r .dir <<<"$svc")"
  run_area "dotnet-$name" "$(key "dotnet-$name" "$dir" "dotnet-$name")" dotnet_area "$svc"
done < <(area dotnet_services | jq -c '.[]')
for a in scripts web; do
  [ "$(area "$a")" = true ] && run_area "$a" "$(key "$a" "" "$a")" "${a}_area"
done
[ "$(area e2e)" = true ] && run_area e2e "$(key e2e "$scope" e2e)" e2e_area "$scope"
[ "$(area hosted)" = true ] && echo "skip hosted (CI's hosted job runs infra/hosted/check.sh)"

if [ "$failed" = 0 ]; then echo "gate: passed"; exit 0; fi
echo "gate: failed"; exit 1
