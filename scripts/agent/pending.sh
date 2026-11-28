#!/usr/bin/env bash
# Prints the single most important thing the unattended runner should do next, or nothing when idle.
#
#   bash scripts/agent/pending.sh
#
# One line, highest priority first:
#   fix <pr> <slice>     the reviewer asked for changes on the head commit (at most 3 changes verdicts so far)
#                        and the bot has not answered since, or the owner commented after the PR was last
#                        handled (the latest of head commit, verdict and bot reply, a non-verdict comment by
#                        the bot), or ci-ok failed on the head commit after the implementer last handled it (the
#                        later of head commit and bot reply) and at most 3 of the PR's commits have a failed
#                        ci-ok. The implementer ends its run when the PR is open, so CI is read here, not
#                        watched by it. Past the third failed commit no fix is offered: the merge gate refuses
#                        `ci-ok failed` and the runner hands the PR to the owner
#   cap <pr> <slice>     the reviewer asked for changes a fourth time or more: the round cap is reached. Not
#                        offered for a PR already labelled owner-blocked, which has been handed over. No other
#                        label hides it: has-decision is the implementer's, set long before
#   review <pr> <slice>  no verdict for the head commit, or a bot reply newer than a changes verdict (the fix
#                        run answered without a commit; the reviewer reads the reply and issues a new verdict)
#   merge <pr> <slice>   an approve verdict for the head commit
#   owner <pr> <slice>   an approve verdict for the head of a PR carrying has-decision and none of the three owner
#                        labels: the runner hands it to the owner, who merges it. Before resume and new, so a
#                        queue of slices does not delay the owner label and the mention
#   resume <slice>       status In progress and no open PR
#   new <slice>          the slice next.sh --number proposes
#   owner <pr> <slice>   an approve verdict for the head of a PR carrying owner-blocked: the merge gate's reasons
#                        say whether it has become ready. Offered every loop for as long as it carries the label,
#                        so it has the lowest priority and never starves other work
# An approve verdict for the head of a PR carrying owner-merge or owner-decision, and not owner-blocked, yields
# nothing: the PR has been handed over for this head, since the runner takes those two labels off as soon as the
# head has no approve. So the first owner item is offered until the runner has labelled the PR, once per head when
# the gate leaves it to the owner, and cannot starve resume and new.
# The owner labels are owner-merge, owner-decision and owner-blocked, which the runner sets. has-decision is the
# implementer's mark that a decision waits for the owner, and is read here like an owner label. The merge gate
# refuses on any of the four. in-review, the runner's label for a head with no approve, is not read here.
# Owner input is any issue comment, inline review comment or submitted review (not pending) by the owner.
# Only open PRs by the bot are considered. A PR whose slice is Blocked on the board (board.sh get) yields nothing,
# unless there is owner input, which is fix (R51). An owner label or has-decision blocks merging only (R42): such a PR gets fix and review
# like any other, and an approve for its head gives owner, or nothing, instead of merge. Within a priority, the lowest PR
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
MAX_CI_FIXES=3  # a PR with more commits whose ci-ok failed than this gets no more fix rounds for CI
CHECK="ci-ok"   # read as merge-gate.sh reads it: the GitHub Actions check run of that name
CHECK_APP="github-actions"
OWNER_LABELS='["owner-merge","owner-decision","owner-blocked","has-decision"]'   # the merge gate's list
HANDED_LABELS='["owner-merge","owner-decision"]'   # an approved head carrying one was handed to the owner
BLOCKED_LABEL="owner-blocked"

scripts="${AGENT_SCRIPTS:-$(cd "$(dirname "$0")" && pwd)}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

gh pr list --repo "$OWNER/$REPO_NAME" --state open --author "$BOT" --limit 100 \
  --json number,headRefOid,headRefName,labels,closingIssuesReferences > "$tmp/prs-list.json"

# The head commit's date, one small read per pull request. Asking the list for `commits` instead pulls
# every commit's authors for every pull request, which exceeds GitHub's GraphQL node limit.
# A file, not `for sha in $(jq ...)`: a failing command there yields an empty list instead of stopping.
jq -r '.[].headRefOid' "$tmp/prs-list.json" > "$tmp/shas" \
  || { echo "could not read the pull request list" >&2; exit 1; }
echo '{}' > "$tmp/dates.json"
while read -r sha; do
  date="$(gh api "repos/$OWNER/$REPO_NAME/commits/$sha" --jq .commit.committer.date)"
  jq --arg s "$sha" --arg d "$date" '.[$s] = $d' "$tmp/dates.json" > "$tmp/dates.next"
  mv "$tmp/dates.next" "$tmp/dates.json"
done < "$tmp/shas"
jq --slurpfile d "$tmp/dates.json" \
  'map(. + {commits: (($d[0][.headRefOid] // "") as $c | if $c == "" then [] else [{committedDate: $c}] end)})' \
  "$tmp/prs-list.json" > "$tmp/prs.json" \
  || { echo "could not read the pull request list" >&2; exit 1; }

# ci_failed_at <sha>: prints when ci-ok failed on the commit, in epoch seconds, or nothing when it has not (passed,
# pending or absent). A failed run is a completed one that concluded other than success, as merge-gate.sh reads it.
# Exits non zero when the read fails.
ci_failed_at() {
  gh api "repos/$OWNER/$REPO_NAME/commits/$1/check-runs?check_name=$CHECK&per_page=100" --paginate \
    --jq ".check_runs[] | select(.name == \"$CHECK\" and .app.slug == \"$CHECK_APP\") | \"\(.status) \(.conclusion) \(.completed_at)\"" \
    > "$tmp/runs" || { echo "could not read the check runs of $1" >&2; return 2; }
  awk '$1 == "completed" && $2 != "success" { print $3 }' "$tmp/runs" | sort | tail -n1 > "$tmp/failed_at"
  [ -s "$tmp/failed_at" ] || return 0
  jq -R -r 'fromdateiso8601' "$tmp/failed_at" || { echo "unreadable completed_at on $1" >&2; return 2; }
}
# ci_fix <pr> <head sha> <implementer handled at>: true when ci-ok failed on the head after the implementer last
# handled the PR, and at most MAX_CI_FIXES of its commits have a failed ci-ok. A failed read stops the run.
ci_fix() {
  local at failed=0 c
  at="$(ci_failed_at "$2")" || exit 1
  [ -n "$at" ] && [ "$at" -gt "$3" ] || return 1
  gh api "repos/$OWNER/$REPO_NAME/pulls/$1/commits?per_page=100" --paginate --jq '.[].sha' > "$tmp/pr-commits" \
    || { echo "pr #$1: could not read its commits" >&2; exit 1; }
  while read -r c; do
    [ -n "$c" ] || continue
    at="$(ci_failed_at "$c")" || exit 1
    [ -z "$at" ] || failed=$((failed + 1))
  done < "$tmp/pr-commits"
  [ "$failed" -le "$MAX_CI_FIXES" ]
}

# One candidate per line: "<priority> <pr> <kind> <slice>"; the smallest sorts first.
: > "$tmp/candidates"
open_slices=" "

jq -r --argjson owner_labels "$OWNER_LABELS" --argjson handed_labels "$HANDED_LABELS" --arg blocked "$BLOCKED_LABEL" '.[] | [.number, .headRefOid,
      ([(.closingIssuesReferences[0].number // empty | tostring),
        ((.headRefName // "") | capture("^(?<n>[0-9]+)-").n)?] | .[0] // "-"),
      (any(.labels[]; .name as $n | $owner_labels | index($n) != null)), (any(.labels[]; .name == $blocked)),
      (any(.labels[]; .name as $n | $handed_labels | index($n) != null)),
      (.commits[-1].committedDate // error("pull request #\(.number) has no commits"))] | @tsv' "$tmp/prs.json" > "$tmp/prs.tsv" \
  || { echo "could not read the pull request list" >&2; exit 1; }

while IFS=$'\t' read -r pr sha slice labelled blocked handed committed; do
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
  # <1 if a bot reply is newer than the verdict> <when the implementer last handled it: the later of head commit
  # and latest bot reply, in epoch seconds>".
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
    | ([$c] + $replies | max) as $implementer
    | "\($new | length) \([$replies[] | select($v != null and . > $v)] | length | if . > 0 then 1 else 0 end) \($implementer | floor)"')" \
    || { echo "pr #$pr: could not compare timestamps" >&2; exit 1; }
  read -r owner_new reply_after implementer_at <<<"$handled"

  # R51: a PR whose slice is Blocked waits for the owner, so the queue moves past it; owner input newer than the
  # last handling is how the owner wakes it (fix). Not `$(...)` inside an `if`: a failed board read stops the run.
  status="$(bash "$scripts/board.sh" get "$slice")"
  if [ "$status" = Blocked ]; then
    [ "$owner_new" -gt 0 ] && echo "1 $pr fix $slice" >> "$tmp/candidates"
    continue
  fi

  if [ "$owner_new" -gt 0 ]; then
    echo "1 $pr fix $slice" >> "$tmp/candidates"
  elif ci_fix "$pr" "$sha" "$implementer_at"; then
    echo "1 $pr fix $slice" >> "$tmp/candidates"
  elif [ "$vsha" != "$sha" ] || [ "$kind" = none ]; then
    echo "3 $pr review $slice" >> "$tmp/candidates"
  elif [ "$kind" = changes ] && [ "$count" -le "$MAX_CHANGES" ] && [ "$reply_after" = 1 ]; then
    echo "3 $pr review $slice" >> "$tmp/candidates"
  elif [ "$kind" = changes ] && [ "$count" -le "$MAX_CHANGES" ]; then
    echo "1 $pr fix $slice" >> "$tmp/candidates"
  elif [ "$kind" = changes ] && [ "$blocked" = true ]; then
    :   # at the cap and already handed to the owner: nothing to offer, or the runner would loop on it
  elif [ "$kind" = changes ]; then
    echo "2 $pr cap $slice" >> "$tmp/candidates"
  elif [ "$kind" = approve ] && [ "$blocked" = true ]; then
    # Offered every loop while it stays stuck, and the runner waits the poll interval on it: last, or it would
    # hold up resume and new for good. Checked before the handed over labels, so a PR carrying both is settled.
    echo "8 $pr owner $slice" >> "$tmp/candidates"
  elif [ "$kind" = approve ] && [ "$handed" = true ]; then
    :   # handed to the owner for this head: nothing to offer, or the runner would wait on it every loop
  elif [ "$kind" = approve ] && [ "$labelled" = true ]; then
    echo "5 $pr owner $slice" >> "$tmp/candidates"
  elif [ "$kind" = approve ]; then
    echo "4 $pr merge $slice" >> "$tmp/candidates"
  fi
done < "$tmp/prs.tsv"

# Without an open PR: an In progress slice is resumed, otherwise the next Ready one is started.
bash "$scripts/next.sh" --machine > "$tmp/board.txt"
while read -r kind slice; do
  case "$kind" in
    inprogress) case "$open_slices" in *" $slice "*) ;; *) echo "6 0 resume $slice" >> "$tmp/candidates" ;; esac ;;
    next) echo "7 0 new $slice" >> "$tmp/candidates" ;;
  esac
done < "$tmp/board.txt"

best="$(sort -k1,1n -k2,2n "$tmp/candidates" | head -n1)"
[ -n "$best" ] || exit 0
read -r _ pr kind slice <<<"$best"
case "$kind" in
  resume|new) echo "$kind $slice" ;;
  *) echo "$kind $pr $slice" ;;
esac
