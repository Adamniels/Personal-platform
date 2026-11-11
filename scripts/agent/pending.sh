#!/usr/bin/env bash
# Prints the single most important thing the unattended runner should do next, or nothing when idle.
#
#   bash scripts/agent/pending.sh
#
# One line, highest priority first:
#   fix <pr> <slice>     the reviewer asked for changes on the head commit (at most 3 changes verdicts so far)
#                        and the bot has not answered since, or the owner commented after the PR was last
#                        handled (the latest of head commit, verdict and bot reply, a non-verdict comment by
#                        the bot)
#   cap <pr> <slice>     the reviewer asked for changes a fourth time or more: the round cap is reached. Not
#                        offered for a PR already labelled needs-owner, which has been handed over
#   review <pr> <slice>  no verdict for the head commit, or a bot reply newer than a changes verdict (the fix
#                        run answered without a commit; the reviewer reads the reply and issues a new verdict)
#   merge <pr> <slice>   an approve verdict for the head commit
#   resume <slice>       status In progress and no open PR
#   new <slice>          the slice next.sh --number proposes
#   owner <pr> <slice>   an approve verdict for the head of a PR labelled needs-owner: the owner merges it.
#                        Lowest priority, so it never starves other work
# Owner input is any issue comment, inline review comment or submitted review (not pending) by the owner.
# Only open PRs by the bot are considered. A PR whose slice is Blocked on the board (board.sh get) yields nothing,
# unless there is owner input, which is fix (R51). needs-owner blocks merging only (R42): such a PR gets fix and review
# like any other, and an approve for its head gives owner instead of merge. Within a priority, the lowest PR
# number first. The slice is the PR's first closing issue reference, else the number its head branch name
# starts with (`<n>-`); a PR with neither is skipped with a warning on stderr.
# Verdicts come from verdict.sh (`<approve|changes|none> <sha|0> <changes count> <created_at|->`), each PR's slice
# status from `board.sh get`, and the board from `next.sh --machine`, all looked up in $AGENT_SCRIPTS (default: this script's directory) so tests
# can fake them.
# Exit codes: 0 ok (no output means idle), 1 when a GitHub read, board.sh or verdict.sh fails (nothing is printed; the
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
      (any(.labels[]; .name == "needs-owner")), (.commits[-1].committedDate // error("pull request #\(.number) has no commits"))] | @tsv' "$tmp/prs.json" > "$tmp/prs.tsv" \
  || { echo "could not read the pull request list" >&2; exit 1; }

while IFS=$'\t' read -r pr sha slice labelled committed; do
  open_slices="$open_slices$slice "
  if [ "$slice" = - ]; then echo "pr #$pr closes no issue; skipped" >&2; continue; fi

  # Not `$(...)` inside an `if`: a failing verdict.sh must stop the run (set -e), not read as "no verdict".
  line="$("$scripts/verdict.sh" "$pr")"
  read -r kind vsha count vtime <<<"$line"
  case "$kind" in approve|changes|none) ;; *) echo "pr #$pr: unreadable verdict line: $line" >&2; exit 1 ;; esac
  [[ "$count" =~ ^[0-9]+$ ]] && [[ "$vtime" =~ ^(-|[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z)$ ]] || { echo "pr #$pr: unreadable verdict line: $line" >&2; exit 1; }

  # Issue comments (which hold the bot's replies and the owner's comments) apart from inline comments and reviews.
  gh api "repos/$OWNER/$REPO_NAME/issues/$pr/comments" --paginate > "$tmp/issue.json"
  : > "$tmp/other.json"
  for endpoint in "pulls/$pr/comments" "pulls/$pr/reviews"; do
    gh api "repos/$OWNER/$REPO_NAME/$endpoint" --paginate >> "$tmp/other.json"
  done
  # R30: the PR was last handled at the latest of the head commit, the latest verdict and the latest bot reply (a
  # comment by the bot that is not a verdict, by verdict.sh's rule). Prints "<owner input newer than that>
  # <1 if a bot reply is newer than the verdict>".
  handled="$(jq -n -r --slurpfile issue "$tmp/issue.json" --slurpfile other "$tmp/other.json" \
    --arg owner "$OWNER" --arg bot "$BOT" --arg vtime "$vtime" --arg committed "$committed" '
    def secs: fromdateiso8601;
    [$issue[][] | select(.user.login == $bot)
      | select(((.updated_at != null and .updated_at == .created_at)
                and (((.body // "") | split("\n")[0])
                     | test("\\A<!-- verdict: (approve|changes) sha=[0-9a-f]{40} -->\\z"))) | not)
      | .created_at | secs] as $replies
    | ($committed | secs) as $c
    | (if $vtime == "-" then null else ($vtime | secs) end) as $v
    | ([$c, $v] | map(select(. != null)) + $replies | max) as $handled
    | [($issue[][], $other[][]) | select(.user.login == $owner and (.state // "") != "PENDING")
        | (.submitted_at // .created_at) | select(. != null) | secs | select(. > $handled)] as $new
    | "\($new | length) \([$replies[] | select($v != null and . > $v)] | length | if . > 0 then 1 else 0 end)"')" \
    || { echo "pr #$pr: could not compare timestamps" >&2; exit 1; }
  read -r owner_new reply_after <<<"$handled"

  # R51: a PR whose slice is Blocked waits for the owner, so the queue moves past it; owner input newer than the
  # last handling is how the owner wakes it (fix). Not `$(...)` inside an `if`: a failed board read stops the run.
  status="$(bash "$scripts/board.sh" get "$slice")"
  if [ "$status" = Blocked ]; then
    [ "$owner_new" -gt 0 ] && echo "1 $pr fix $slice" >> "$tmp/candidates"
    continue
  fi

  if [ "$owner_new" -gt 0 ]; then
    echo "1 $pr fix $slice" >> "$tmp/candidates"
  elif [ "$vsha" != "$sha" ] || [ "$kind" = none ]; then
    echo "3 $pr review $slice" >> "$tmp/candidates"
  elif [ "$kind" = changes ] && [ "$count" -le "$MAX_CHANGES" ] && [ "$reply_after" = 1 ]; then
    echo "3 $pr review $slice" >> "$tmp/candidates"
  elif [ "$kind" = changes ] && [ "$count" -le "$MAX_CHANGES" ]; then
    echo "1 $pr fix $slice" >> "$tmp/candidates"
  elif [ "$kind" = changes ] && [ "$labelled" = true ]; then
    :   # at the cap and already handed to the owner: nothing to offer, or the runner would loop on it
  elif [ "$kind" = changes ]; then
    echo "2 $pr cap $slice" >> "$tmp/candidates"
  elif [ "$kind" = approve ] && [ "$labelled" = true ]; then
    echo "7 $pr owner $slice" >> "$tmp/candidates"
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
