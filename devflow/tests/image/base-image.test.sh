#!/usr/bin/env bash
# Checks the worker's base image, devflow/worker.Dockerfile, as built: the agent cannot read the
# worker's files, and the worker, Claude Code, git and node run; Claude Code's commit attribution is
# off for the agent; and a project's image built FROM it reports the base image and the recipe's
# version the command on the project's page passes. See "The base image" in devflow/README.md.
#
# It builds the image itself, under a tag of its own that it removes again, never under the hosted
# copy's name; with Docker's cache warm from a release's build this takes seconds.
# infra/hosted/check.sh runs it. It needs Docker and nothing else.
#
# Run from anywhere: bash devflow/tests/image/base-image.test.sh
# Each case prints `ok <name>` or `FAIL <name>`, and the script exits non zero if any failed.
# Written for bash 3.2, the one macOS ships.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd -P)"
tmp="$(mktemp -d)"
suffix="$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')"
image="devflow-worker-imagetest:$suffix"
recipe_image="devflow-recipe-imagetest:$suffix"
pass=0
fail=0

cleanup() {
  docker image rm -f "$recipe_image" "$image" > /dev/null 2>&1
  rm -rf "$tmp"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

ok() { echo "ok $1"; pass=$((pass + 1)); }
bad() {
  echo "FAIL $1"
  [ -n "${2:-}" ] && printf '     %s\n' "$2"
  fail=$((fail + 1))
}

# as <user> <command>...: the command in a fresh container of the image, as the user, its output and
# its error together.
as() {
  local user="$1"
  shift
  docker run --rm --user "$user" --entrypoint "$1" "$image" "${@:2}" 2>&1
}

echo "-- building $image"
if ! docker build -f "$repo/devflow/worker.Dockerfile" -t "$image" "$repo" > "$tmp/build.log" 2>&1; then
  tail -n 30 "$tmp/build.log" >&2
  bad TheImageBuilds "docker build failed, its log is above"
  echo "$pass passed, $fail failed"
  exit 1
fi
ok TheImageBuilds

# --- TheAgentCannotReadTheWorkersFiles -----------------------------------------------------------
# As agent, listing or reading /run/devflow/ fails, while root, the worker, can write there; the
# worker binary runs, refusing to start without its settings, exit 2, as it does; Claude Code
# answers 2.1.325; git and node run.
problems=""
got="$(as agent sh -c 'ls /run/devflow/ && echo listed')"
case "$got" in *listed*) problems="$problems agent-lists-/run/devflow:[$got]" ;; esac
got="$(as root sh -c 'echo secret > /run/devflow/job.env && chmod 600 /run/devflow/job.env && stat -c "%U %a" /run/devflow')"
[ "$got" = "root 700" ] || problems="$problems /run/devflow-is:[$got]"
got="$(docker run --rm --entrypoint sh "$image" -c \
  'echo secret > /run/devflow/job.env && setpriv --reuid agent --regid agent --init-groups --reset-env cat /run/devflow/job.env && echo read' 2>&1)"
case "$got" in *read*) problems="$problems agent-reads-a-secret:[$got]" ;; esac
got="$(docker run --rm "$image" 2>&1)"
code=$?
if [ "$code" -ne 2 ] || ! printf '%s' "$got" | grep -q 'DEVFLOW_URL is not set'; then
  problems="$problems worker:exit-$code:[$got]"
fi
got="$(as agent claude --version)"
case "$got" in "2.1.325 "* | 2.1.325) ;; *) problems="$problems claude:[$got]" ;; esac
got="$(as agent git --version)"
case "$got" in "git version "*) ;; *) problems="$problems git:[$got]" ;; esac
got="$(as agent node --version)"
case "$got" in v26.*) ;; *) problems="$problems node:[$got]" ;; esac
got="$(as agent id -u)"
[ "$got" = 1000 ] || problems="$problems agent-uid:[$got]"
if [ -z "$problems" ]; then
  ok TheAgentCannotReadTheWorkersFiles
else
  bad TheAgentCannotReadTheWorkersFiles "$problems"
fi

# --- TheWorkerRunsAsRootAndTheAgentOwnsTheWork ---------------------------------------------------
got="$(docker image inspect --format '{{.Config.User}}|{{json .Config.Entrypoint}}|{{.Config.WorkingDir}}' "$image")"
owner="$(as agent stat -c '%U' /work)"
if [ "$got" = '|["/usr/local/bin/devflow-worker"]|/work' ] && [ "$owner" = agent ]; then
  ok TheWorkerRunsAsRootAndTheAgentOwnsTheWork
else
  bad TheWorkerRunsAsRootAndTheAgentOwnsTheWork "user|entrypoint|workdir: $got, /work owned by $owner"
fi

# --- CommitAttributionIsOff ----------------------------------------------------------------------
# The agent's own settings turn off Claude Code's Co-Authored-By trailer and pull request line.
got="$(as agent sh -c 'jq -c .attribution ~/.claude/settings.json')"
if [ "$got" = '{"commit":"","pr":"","sessionUrl":false}' ]; then
  ok CommitAttributionIsOff
else
  bad CommitAttributionIsOff "attribution is $got"
fi

# --- AProjectsImageReportsItsBaseAndRecipe -------------------------------------------------------
# A recipe as a project writes one, built as the page's command builds it, holds the two variables
# the worker sends in hello.
printf 'ARG DEVFLOW_BASE\nFROM ${DEVFLOW_BASE}\nRUN true\n' > "$tmp/Dockerfile"
if docker build --build-arg "DEVFLOW_BASE=$image" --build-arg DEVFLOW_RECIPE_VERSION=0123abc \
  -t "$recipe_image" - < "$tmp/Dockerfile" > "$tmp/recipe.log" 2>&1; then
  got="$(docker run --rm --entrypoint sh "$recipe_image" -c 'echo "$DEVFLOW_BASE_IMAGE|$DEVFLOW_RECIPE_VERSION"' 2>&1)"
  if [ "$got" = "$image|0123abc" ]; then
    ok AProjectsImageReportsItsBaseAndRecipe
  else
    bad AProjectsImageReportsItsBaseAndRecipe "the image reports $got"
  fi
else
  bad AProjectsImageReportsItsBaseAndRecipe "the recipe did not build: $(tail -n 5 "$tmp/recipe.log" | tr '\n' ' ')"
fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
