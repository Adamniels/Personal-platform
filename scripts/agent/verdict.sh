#!/usr/bin/env bash
# Prints the reviewer's latest verdict on a pull request.
#
#   bash scripts/agent/verdict.sh <pr-number>
#
# One line: `<approve|changes|none> <sha|0> <count of changes verdicts> <created_at|->`.
# A verdict is a PR comment by mac-mini-agent whose first line is exactly the marker
#   <!-- verdict: approve sha=<40 lowercase hex> -->   or   <!-- verdict: changes sha=<40 lowercase hex> -->
# and the newest such comment (by created_at) is the verdict, printed with its sha and its created_at exactly as
# GitHub returns it. Anything else is ignored: other authors, a marker on a later line (a quoting reply), a
# different word, a malformed sha, extra spaces or text, a trailing carriage return, and any comment that was
# edited (updated_at differs from created_at), since an edited verdict is not the one the reviewer wrote for
# that commit. With no verdict it prints `none 0 <count> -`. The count is every valid changes marker by mac-mini-agent, whatever its sha.
# This line is what lets merge-gate.sh merge, so it fails closed: anything unreadable is an error, never a verdict.
# Exit codes: 0 ok, 1 GitHub error or unreadable response (nothing on stdout), 2 usage error.
set -euo pipefail

OWNER="Adamniels"
REPO_NAME="Personal-platform"
BOT="mac-mini-agent"

[ $# -eq 1 ] && [[ "$1" =~ ^[1-9][0-9]*$ ]] || { echo "usage: verdict.sh <pr-number>" >&2; exit 2; }
pr="$1"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# --paginate prints one JSON array per page; jq -s joins them.
gh api "repos/$OWNER/$REPO_NAME/issues/$pr/comments" --paginate > "$tmp/comments.json" \
  || { echo "pr #$pr: could not read the comments" >&2; exit 1; }

# Captured before printing, so a jq failure prints nothing on stdout.
line="$(jq -s -r --arg bot "$BOT" '
  [ .[][]
    | select(.user.login == $bot and .updated_at != null and .updated_at == .created_at)
    | ((.body // "") | split("\n")[0]) as $first
    | ($first | capture("\\A<!-- verdict: (?<kind>approve|changes) sha=(?<sha>[0-9a-f]{40}) -->\\z")?) as $m
    | select($m != null)
    | {kind: $m.kind, sha: $m.sha, created_at, id} ]
  | sort_by([.created_at, .id]) as $v
  | ([$v[] | select(.kind == "changes")] | length) as $count
  | if ($v | length) == 0 then "none 0 \($count) -"
    else ($v[-1] | "\(.kind) \(.sha) \($count) \(.created_at)") end' "$tmp/comments.json")" \
  || { echo "pr #$pr: could not read the comments" >&2; exit 1; }

echo "$line"
