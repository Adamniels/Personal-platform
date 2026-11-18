#!/usr/bin/env bash
# Checks the worker's base image, devflow/worker.Dockerfile, as built: the agent cannot read a
# job's secrets, in /run/devflow/ or in root's environment, nor write where root hands it bundles,
# /run/devflow-handover/; the worker, Claude Code, git and node run; Claude Code's commit
# attribution is off for the agent; and a project's image built FROM it reports the base image and the recipe's
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

# --- TheAgentCannotReadTheJobsSecrets -----------------------------------------------------------
# What a job gives the worker is root's alone: as agent, listing or reading /run/devflow/ fails,
# while root, the worker, can write there; and root's environment, where a git command run as root
# holds the push key, cannot be read through /proc, as /proc/<pid>/environ is its owner's.
problems=""
got="$(as agent sh -c 'ls /run/devflow/ && echo listed')"
case "$got" in *listed*) problems="$problems agent-lists-/run/devflow:[$got]" ;; esac
got="$(as root sh -c 'echo secret > /run/devflow/job.env && chmod 600 /run/devflow/job.env && stat -c "%U %a" /run/devflow')"
[ "$got" = "root 700" ] || problems="$problems /run/devflow-is:[$got]"
got="$(docker run --rm --entrypoint sh "$image" -c \
  'echo secret > /run/devflow/job.env && setpriv --reuid agent --regid agent --init-groups --reset-env cat /run/devflow/job.env && echo read' 2>&1)"
case "$got" in *read*) problems="$problems agent-reads-a-secret:[$got]" ;; esac
# A root process holding a key in its environment, as git does for a push, and the agent reading
# every process's environment it can.
got="$(docker run --rm -e DEVFLOW_PUSH_KEY_IMAGETEST=ghs_imagetestkey --entrypoint sh "$image" -c \
  'sleep 30 & pid=$!; setpriv --reuid agent --regid agent --init-groups --reset-env sh -c "cat /proc/1/environ /proc/$pid/environ /proc/self/environ 2>&1; for f in /proc/[0-9]*/environ; do cat \$f 2>/dev/null; done" | tr "\0" "\n"; kill $pid' 2>&1)"
case "$got" in *ghs_imagetestkey*) problems="$problems agent-reads-roots-environment:[$got]" ;; esac
case "$got" in *"Permission denied"*) ;; *) problems="$problems no-refusal-seen:[$got]" ;; esac
if [ -z "$problems" ]; then
  ok TheAgentCannotReadTheJobsSecrets
else
  bad TheAgentCannotReadTheJobsSecrets "$problems"
fi

# --- TheWorkerAndItsToolsRun ---------------------------------------------------------------------
# The worker binary runs, refusing to start without its settings, exit 2, as it does; Claude Code
# answers 2.1.325; git and node run, and git-upload-pack, which the worker's fetch from the agent's
# copy runs as agent, is on agent's PATH.
problems=""
got="$(docker run --rm "$image" 2>&1)"
code=$?
if [ "$code" -ne 2 ] || ! printf '%s' "$got" | grep -q 'DEVFLOW_URL is not set'; then
  problems="$problems worker:exit-$code:[$got]"
fi
got="$(as agent claude --version)"
case "$got" in "2.1.325 "* | 2.1.325) ;; *) problems="$problems claude:[$got]" ;; esac
got="$(as agent git --version)"
case "$got" in "git version "*) ;; *) problems="$problems git:[$got]" ;; esac
got="$(as agent sh -c 'command -v git-upload-pack')"
case "$got" in /*git-upload-pack) ;; *) problems="$problems git-upload-pack:[$got]" ;; esac
got="$(as agent node --version)"
case "$got" in v26.*) ;; *) problems="$problems node:[$got]" ;; esac
got="$(as agent id -u)"
[ "$got" = 1000 ] || problems="$problems agent-uid:[$got]"
if [ -z "$problems" ]; then
  ok TheWorkerAndItsToolsRun
else
  bad TheWorkerAndItsToolsRun "$problems"
fi

# --- TheWorkerRunsAsRootAndTheAgentOwnsTheWork ---------------------------------------------------
got="$(docker image inspect --format '{{.Config.User}}|{{json .Config.Entrypoint}}|{{.Config.WorkingDir}}' "$image")"
owner="$(as agent stat -c '%U' /work)"
if [ "$got" = '|["/usr/local/bin/devflow-worker"]|/work' ] && [ "$owner" = agent ]; then
  ok TheWorkerRunsAsRootAndTheAgentOwnsTheWork
else
  bad TheWorkerRunsAsRootAndTheAgentOwnsTheWork "user|entrypoint|workdir: $got, /work owned by $owner"
fi

# --- TheAgentCannotWriteWhereRootHandsOver -------------------------------------------------------
# Root writes the bundles the agent clones from in /run/devflow-handover/, root's, mode 755: the
# agent can read what root put there, and can put nothing there itself, not even a link, so no name
# root writes at is one the agent could point elsewhere.
problems=""
got="$(as root stat -c '%U %a' /run/devflow-handover)"
[ "$got" = "root 755" ] || problems="$problems /run/devflow-handover-is:[$got]"
got="$(as agent sh -c 'ln -s /etc/passwd /run/devflow-handover/x.bundle && echo linked; mkdir /run/devflow-handover/job && echo made')"
case "$got" in *linked* | *made*) problems="$problems agent-writes-the-handover:[$got]" ;; esac
got="$(docker run --rm --entrypoint sh "$image" -c \
  'mkdir -m 755 /run/devflow-handover/job && echo bundle > /run/devflow-handover/job/copy.bundle && chmod 644 /run/devflow-handover/job/copy.bundle && setpriv --reuid agent --regid agent --init-groups --reset-env cat /run/devflow-handover/job/copy.bundle' 2>&1)"
[ "$got" = bundle ] || problems="$problems agent-cannot-read-a-bundle:[$got]"
if [ -z "$problems" ]; then
  ok TheAgentCannotWriteWhereRootHandsOver
else
  bad TheAgentCannotWriteWhereRootHandsOver "$problems"
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
