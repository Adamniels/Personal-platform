#!/usr/bin/env bash
# Publishes screenshots for a pull request and prints Markdown lines that embed them.
#
#   bash scripts/agent/screenshots.sh <pr-number> <png>...
#
# GitHub has no API for attaching images to a pull request, so each file is committed to the
# disposable orphan branch `pr-screenshots` at <pr>/<basename> and embedded through a
# blob/...?raw=true URL, which renders for signed-in viewers of this private repository. The branch
# is created, without a parent, when `origin` has none. Work happens in a temporary git worktree
# of the repository around the current directory; the commit is built with commit-tree and pushed to
# `origin` by object name, with the identity already configured there, so no local branch is created.
# The current branch, HEAD, index and working tree are never touched, and the worktree is removed on
# exit, also on failure. Pushing an identical file again is fine.
# Prints one line per file: ![<name without .png>](<url>)
# Exit codes: 0 ok, 1 git error, 2 usage error.
set -euo pipefail

OWNER="Adamniels"
REPO_NAME="Personal-platform"
BRANCH="pr-screenshots"

if [ "$#" -lt 2 ] || ! [[ "$1" =~ ^[1-9][0-9]*$ ]]; then
  echo "usage: screenshots.sh <pr-number> <png>..." >&2
  exit 2
fi
pr="$1"
shift
for f in "$@"; do
  [[ "$f" == *.png ]] || { echo "screenshots.sh: not a .png file: $f" >&2; exit 2; }
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

# --no-checkout: nothing from the caller's branch lands in the worktree, and its index starts empty.
# No named local branch is ever created, so the caller's repository gains no refs; the commit is
# built with commit-tree and pushed by object name.
git -C "$root" worktree add --detach --no-checkout -q "$wt" HEAD || exit 1
parent=()
if [ -n "$remote" ]; then
  git -C "$wt" fetch -q origin "$BRANCH" || exit 1
  git -C "$wt" read-tree FETCH_HEAD || exit 1
  parent=(-p "$(git -C "$wt" rev-parse FETCH_HEAD)")
fi

mkdir -p "$wt/$pr"
for f in "$@"; do
  cp "$f" "$wt/$pr/$(basename "$f")"
done
git -C "$wt" add -A -- "$pr" || exit 1
tree="$(git -C "$wt" write-tree)" || exit 1

# Nothing new to publish when the tree equals the branch tip's tree.
if [ -z "$remote" ] || [ "$tree" != "$(git -C "$wt" rev-parse "FETCH_HEAD^{tree}")" ]; then
  commit="$(git -C "$wt" -c core.hooksPath=/dev/null commit-tree "$tree" ${parent[@]+"${parent[@]}"} -m "Screenshots for #$pr")" || exit 1
  git -C "$wt" -c core.hooksPath=/dev/null push -q origin "$commit:refs/heads/$BRANCH" || exit 1
fi

for f in "$@"; do
  name="$(basename "$f")"
  echo "![${name%.png}](https://github.com/$OWNER/$REPO_NAME/blob/$BRANCH/$pr/$name?raw=true)"
done
