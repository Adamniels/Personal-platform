#!/usr/bin/env bash
# Publishes screenshots for a pull request and prints Markdown lines that embed them.
#
#   bash scripts/agent/screenshots.sh <pr-number> <png>...
#
# GitHub has no API for attaching images to a pull request, so each file is committed to the
# disposable orphan branch `pr-screenshots` at <pr>/<basename> and embedded through a
# blob/...?raw=true URL, which renders for signed-in viewers of this private repository. The branch
# is created, without a parent, when `origin` has none. Work happens in a temporary git worktree
# of the repository around the current directory, pushed to `origin` with the commit identity
# already configured there; the current branch, HEAD, index and working tree are never touched,
# and the worktree is removed on exit, also on failure. Pushing an identical file again is fine.
# Prints one line per file: ![<name without .png>](<url>)
# Exit codes: 0 ok, 1 git error, 2 usage error.
set -euo pipefail

OWNER="Adamniels"
REPO_NAME="Personal-platform"
BRANCH="pr-screenshots"

if [ "$#" -lt 2 ] || ! [[ "$1" =~ ^[0-9]+$ ]]; then
  echo "usage: screenshots.sh <pr-number> <png>..." >&2
  exit 2
fi
pr="$1"
shift
for f in "$@"; do
  [ -f "$f" ] || { echo "screenshots.sh: no such file: $f" >&2; exit 2; }
done

root="$(git rev-parse --show-toplevel)" || exit 1
tmp="$(mktemp -d)"
wt="$tmp/wt"
cleanup() {
  git -C "$root" worktree remove --force "$wt" >/dev/null 2>&1 || true
  git -C "$root" worktree prune >/dev/null 2>&1 || true
  rm -rf "$tmp"
}
trap cleanup EXIT

# Fetch failing is not the same as the branch missing: ls-remote tells them apart.
remote="$(git -C "$root" ls-remote --heads origin "$BRANCH")" || { echo "screenshots.sh: cannot reach origin" >&2; exit 1; }

git -C "$root" worktree add --detach -q "$wt" HEAD || exit 1
if [ -n "$remote" ]; then
  git -C "$wt" fetch -q origin "$BRANCH" || exit 1
  git -C "$wt" checkout -q --detach FETCH_HEAD || exit 1
else
  git -C "$wt" switch -q --orphan "$BRANCH" || exit 1
fi

mkdir -p "$wt/$pr"
for f in "$@"; do
  cp "$f" "$wt/$pr/$(basename "$f")"
done
git -C "$wt" add -A -- "$pr" || exit 1

if ! git -C "$wt" diff --cached --quiet; then
  git -C "$wt" commit -q -m "Screenshots for #$pr" || exit 1
fi
git -C "$wt" push -q origin "HEAD:refs/heads/$BRANCH" || exit 1

for f in "$@"; do
  name="$(basename "$f")"
  echo "![${name%.png}](https://github.com/$OWNER/$REPO_NAME/blob/$BRANCH/$pr/$name?raw=true)"
done
