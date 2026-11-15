#!/usr/bin/env bash
# Reads Dockerfiles for the two checks that hold them: infra/hosted/tests/agree.test.sh, which
# infra/hosted/check.sh runs, and scripts/ci/workflows-locked.test.sh, which CI's scripts job runs.
# One copy of the rules, so the two cannot drift.
#
#   dockerfile.sh commands <Dockerfile>
#     Its lines with each backslash continuation joined, comments dropped, and split at && and ;
#     into one command per line.
#   dockerfile.sh unlocked <Dockerfile>...
#     One line per install that does not install from a lockfile, "<file>: <problem>: <command>",
#     with each file named as given, then "checked <n>", the installs that read a lockfile: each
#     cargo build, uv sync, npm ci and dotnet restore. cargo build needs --locked, uv sync --locked
#     and dotnet restore --locked-mode; dotnet build, publish and test need --no-restore, so the one
#     restore is the locked one; npm installs only with npm ci; and uv pip and pip install, which
#     read no lockfile, are refused outright.
set -uo pipefail

commands() {
  awk '
    /^[ \t]*#/ { next }
    { line = line $0 }
    /\\$/ { sub(/\\$/, "", line); next }
    { gsub(/&&|;/, "\n", line); print line; line = "" }
  ' "$1"
}

unlocked() {
  local file checked=0 out
  for file in "$@"; do
    [ -f "$file" ] || { echo "$file does not exist"; continue; }
    out="$(commands "$file" | awk -v f="$file" '
      function has(flag,  i) { for (i = 1; i <= NF; i++) if ($i == flag) return 1; return 0 }
      {
        for (i = 1; i < NF; i++) {
          if ($i == "cargo" && $(i + 1) == "build") { n++; if (!has("--locked")) print f ": cargo build without --locked: " $0 }
          if ($i == "uv" && $(i + 1) == "sync") { n++; if (!has("--locked")) print f ": uv sync without --locked: " $0 }
          if ($i == "uv" && $(i + 1) == "pip") print f ": uv pip installs outside the lock: " $0
          if ($i ~ /^pip3?$/ && $(i + 1) == "install") print f ": pip install installs outside the lock: " $0
          if ($i == "npm" && $(i + 1) ~ /^(i|in|ins|inst|insta|instal|install|isnt|isntall|add|update|up|upgrade|udpate)$/) print f ": npm installs other than npm ci: " $0
          if ($i == "npm" && $(i + 1) == "ci") n++
          if ($i == "dotnet" && $(i + 1) == "restore") { n++; if (!has("--locked-mode")) print f ": dotnet restore without --locked-mode: " $0 }
          if ($i == "dotnet" && $(i + 1) ~ /^(build|publish|test)$/ && !has("--no-restore")) print f ": dotnet " $(i + 1) " restores again without --no-restore: " $0
        }
      }
      END { print "checked " n + 0 }
    ')"
    grep -v '^checked ' <<< "$out"
    checked=$((checked + $(sed -n 's/^checked //p' <<< "$out")))
  done
  echo "checked $checked"
}

case "${1:-}" in
  commands) [ $# -eq 2 ] || { echo "usage: dockerfile.sh commands <Dockerfile>" >&2; exit 2; }; commands "$2" ;;
  unlocked) [ $# -ge 2 ] || { echo "usage: dockerfile.sh unlocked <Dockerfile>..." >&2; exit 2; }; shift; unlocked "$@" ;;
  *) echo "usage: dockerfile.sh commands <Dockerfile> | unlocked <Dockerfile>..." >&2; exit 2 ;;
esac
