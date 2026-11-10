#!/usr/bin/env bash
# Prints the single most important thing the unattended runner should do next, or nothing when idle.
#
#   bash scripts/agent/pending.sh
#
# One line, highest priority first:
#   fix <pr> <slice>     the reviewer asked for changes on the head commit (at most 3 changes verdicts so far),
#                        or the owner commented after both the latest verdict and the head commit
#   cap <pr> <slice>     the reviewer asked for changes a fourth time or more: the round cap is reached
#   review <pr> <slice>  no verdict for the head commit
#   merge <pr> <slice>   an approve verdict for the head commit
#   resume <slice>       status In progress and no open PR
#   new <slice>          the slice next.sh --number proposes
# Owner input is any issue comment, inline review comment or submitted review (not pending) by the owner.
# Only open PRs by the bot are considered. A PR labelled needs-owner is left to the owner: it yields nothing
# unless the owner has commented since, which is fix. Within a priority, the lowest PR number first. The slice
# is the PR's first closing issue reference, else the number its head branch name starts with (`<n>-`); a PR
# with neither is skipped with a warning on stderr.
# Verdicts come from verdict.sh (`<approve|changes|none> <sha|0> <changes count> <created_at|->`) and the board
# from `next.sh --machine`, both looked up in $AGENT_SCRIPTS (default: this script's directory) so tests can fake them.
# Exit codes: 0 ok (no output means idle), 1 when a GitHub read or verdict.sh fails (nothing is printed; the
# runner retries later).
set -euo pipefail

OWNER="Adamniels"
REPO_NAME="Personal-platform"
BOT="mac-mini-agent"
MAX_CHANGES=3   # a PR with more changes verdicts than this has used up its rounds

scripts="${AGENT_SCRIPTS:-$(cd "$(dirname "$0")" && pwd)}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

gh pr list --repo "$OWNER/$REPO_NAME" --state open --author "$BOT" --limit 100 \
  --json number,headRefOid,headRefName,labels,closingIssuesReferences,commits > "$tmp/prs.json"

# One candidate per line: "<priority> <pr> <kind> <slice>"; the smallest sorts first.
: > "$tmp/candidates"
open_slices=" "

jq -r '.[] | [.number, .headRefOid,
      ([(.closingIssuesReferences[0].number // empty | tostring),
        ((.headRefName // "") | capture("^(?<n>[0-9]+)-").n)?] | .[0] // "-"),
      (any(.labels[]; .name == "needs-owner")), (.commits[-1].committedDate)] | @tsv' "$tmp/prs.json" > "$tmp/prs.tsv" \
  || { echo "could not read the pull request list" >&2; exit 1; }

while IFS=$'\t' read -r pr sha slice labelled committed; do
  open_slices="$open_slices$slice "
  if [ "$slice" = - ]; then echo "pr #$pr closes no issue; skipped" >&2; continue; fi

  # Not `$(...)` inside an `if`: a failing verdict.sh must stop the run (set -e), not read as "no verdict".
  line="$("$scripts/verdict.sh" "$pr")"
  read -r kind vsha count vtime <<<"$line"
  case "$kind" in approve|changes|none) ;; *) echo "pr #$pr: unreadable verdict line: $line" >&2; exit 1 ;; esac
  [[ "$count" =~ ^[0-9]+$ ]] || { echo "pr #$pr: unreadable verdict line: $line" >&2; exit 1; }

  : > "$tmp/comments.json"
  for endpoint in "issues/$pr/comments" "pulls/$pr/comments" "pulls/$pr/reviews"; do
    gh api "repos/$OWNER/$REPO_NAME/$endpoint" --paginate >> "$tmp/comments.json"
  done
  owner_new="$(jq -s -r --arg owner "$OWNER" --arg vtime "$vtime" --arg committed "$committed" '
    [.[][] | select(.user.login == $owner and (.state // "") != "PENDING")
        | (.submitted_at // .created_at) | select(. != null) | fromdateiso8601] as $times
    | ($committed | fromdateiso8601) as $c
    | (if $vtime == "-" then $c else ([$c, ($vtime | fromdateiso8601)] | max) end) as $since
    | [$times[] | select(. > $since)] | length' "$tmp/comments.json")"

  if [ "$owner_new" -gt 0 ]; then
    echo "1 $pr fix $slice" >> "$tmp/candidates"
  elif [ "$labelled" = true ]; then
    :
  elif [ "$vsha" != "$sha" ] || [ "$kind" = none ]; then
    echo "3 $pr review $slice" >> "$tmp/candidates"
  elif [ "$kind" = changes ] && [ "$count" -le "$MAX_CHANGES" ]; then
    echo "1 $pr fix $slice" >> "$tmp/candidates"
  elif [ "$kind" = changes ]; then
    echo "2 $pr cap $slice" >> "$tmp/candidates"
  elif [ "$kind" = approve ]; then
    echo "4 $pr merge $slice" >> "$tmp/candidates"
  fi
done < "$tmp/prs.tsv"

# Without an open PR: an In progress slice is resumed, otherwise the next Ready one is started.
bash "$scripts/next.sh" --machine > "$tmp/board.txt"
while read -r kind slice; do
  case "$kind" in
    inprogress) case "$open_slices" in *" $slice "*) ;; *) echo "5 0 resume $slice" >> "$tmp/candidates" ;; esac ;;
    next) echo "6 0 new $slice" >> "$tmp/candidates" ;;
  esac
done < "$tmp/board.txt"

best="$(sort -k1,1n -k2,2n "$tmp/candidates" | head -n1)"
[ -n "$best" ] || exit 0
read -r _ pr kind slice <<<"$best"
case "$kind" in
  resume|new) echo "$kind $slice" ;;
  *) echo "$kind $pr $slice" ;;
esac
