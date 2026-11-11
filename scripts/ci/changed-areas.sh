#!/usr/bin/env bash
# Reads changed paths on stdin, one per line, and prints which CI areas they touch as four
# `area=true|false` lines, ready for $GITHUB_OUTPUT. A change under .github/workflows/ sets every
# area, since a workflow edit can break any of them. Used by the `changes` job in ci.yml.
set -uo pipefail

core=false canary=false scripts=false e2e=false

while IFS= read -r path || [ -n "$path" ]; do
  case "$path" in
    .github/workflows/*) core=true; canary=true; scripts=true; e2e=true ;;
    Core/* | infra/postgres/*) core=true; e2e=true ;;
    canary/*) canary=true; e2e=true ;;
    scripts/* | .claude/settings.json) scripts=true ;;
  esac
  case "$path" in
    infra/* | e2e/*) e2e=true ;;
  esac
done

echo "core=$core"
echo "canary=$canary"
echo "scripts=$scripts"
echo "e2e=$e2e"
