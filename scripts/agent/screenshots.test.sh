#!/usr/bin/env bash
# Offline tests for screenshots.sh. `origin` is a local bare repository in a temp dir, and the script
# runs inside a clone of it, so no test touches GitHub. Run: bash scripts/agent/screenshots.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

pass=0
fail=0
check() { # name, condition result
  if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi
}

git init -q --bare "$tmp/origin.git"
git clone -q "$tmp/origin.git" "$tmp/clone" 2>/dev/null
cd "$tmp/clone" || exit 1
git config user.name "Test"
git config user.email "test@example.com"
git checkout -q -b main
echo hi > README.md
git add README.md
git commit -q -m "init"
git push -q origin main
echo dirty > untracked.txt

printf 'png-a' > "$tmp/a.png"
printf 'png-b' > "$tmp/b.png"
printf 'png-b2' > "$tmp/b2.png"

branch_before="$(git rev-parse --abbrev-ref HEAD)"
head_before="$(git rev-parse HEAD)"
status_before="$(git status --short)"

out="$(bash "$here/screenshots.sh" 12 "$tmp/a.png" 2>&1)"; code=$?
[ "$code" = 0 ]
check "first call exits 0" $?

[ "$out" = '![a](https://github.com/Adamniels/Personal-platform/blob/pr-screenshots/12/a.png?raw=true)' ]
check "prints the exact markdown line" $?

[ "$(git --git-dir="$tmp/origin.git" rev-list --max-parents=0 pr-screenshots | wc -l | tr -d ' ')" = 1 ] \
  && [ "$(git --git-dir="$tmp/origin.git" rev-list --count pr-screenshots)" = 1 ]
check "first call creates an orphan branch with one root commit" $?

git --git-dir="$tmp/origin.git" cat-file -e pr-screenshots:12/a.png
check "the branch holds 12/a.png" $?

out="$(bash "$here/screenshots.sh" 13 "$tmp/b.png" "$tmp/b2.png" 2>&1)"; code=$?
[ "$code" = 0 ] && [ "$(wc -l <<<"$out" | tr -d ' ')" = 2 ] \
  && grep -qF '![b2](https://github.com/Adamniels/Personal-platform/blob/pr-screenshots/13/b2.png?raw=true)' <<<"$out"
check "second call prints one line per file" $?

git --git-dir="$tmp/origin.git" cat-file -e pr-screenshots:13/b.png \
  && git --git-dir="$tmp/origin.git" cat-file -e pr-screenshots:12/a.png
check "second call adds 13/b.png and keeps 12/a.png" $?

bash "$here/screenshots.sh" 12 "$tmp/a.png" >/dev/null 2>&1
check "pushing an identical PNG again does not fail" $?

[ "$(git rev-parse --abbrev-ref HEAD)" = "$branch_before" ] \
  && [ "$(git rev-parse HEAD)" = "$head_before" ] \
  && [ "$(git status --short)" = "$status_before" ] \
  && [ "$(git worktree list | wc -l | tr -d ' ')" = 1 ]
check "branch, HEAD, status and worktree list are unchanged" $?

bash "$here/screenshots.sh" >/dev/null 2>&1; [ $? = 2 ]
check "no arguments exits 2" $?

bash "$here/screenshots.sh" 12 >/dev/null 2>&1; [ $? = 2 ]
check "no files exits 2" $?

bash "$here/screenshots.sh" 12 "$tmp/missing.png" >/dev/null 2>&1; [ $? = 2 ]
check "a missing file exits 2" $?

git remote set-url origin "$tmp/nowhere.git"
bash "$here/screenshots.sh" 14 "$tmp/a.png" >/dev/null 2>&1; code=$?
[ "$code" = 1 ] && [ "$(git rev-parse HEAD)" = "$head_before" ] && [ "$(git worktree list | wc -l | tr -d ' ')" = 1 ]
check "an unreachable origin exits 1 and leaves no worktree" $?

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
