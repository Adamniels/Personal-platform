#!/usr/bin/env bash
# The unattended runner on the Mac mini: asks pending.sh for the next work item, does it, and loops until the stop
# file exists. Started by the runbook as `caffeinate -is bash scripts/agent/run.sh` in tmux.
#
#   bash scripts/agent/run.sh
#
# What runs is main's, never a slice branch's (R43, R48, R49). At start the runner fetches main from GitHub into
# refs/runner/main (it refuses to start if that fails), copies that commit's scripts/agent/run.sh to
# $AGENT_HOME/runner/run.sh and execs the copy, so a branch switch never changes the running script. Every loop it
# fetches again (a failure is a transient pause), restarts itself when main's run.sh differs from the running copy,
# and extracts scripts/agent from refs/runner/main into a fresh temp dir, from which pending.sh, verdict.sh,
# merge-gate.sh, board.sh and gh-guard.sh are called. Every git call ignores the repository's hooks.
#
# Every loop, before asking pending.sh, the runner makes the labels and review-ok true for every open pull request
# by the bot. It lists them, reads each one's verdict (verdict.sh) and the status its head commit has, and writes
# only what differs, so a pull request that is already right costs no write and no further read. Before a write it
# reads the pull request itself, and writes only to one that is open at the listed head: a listing can lag behind a
# merge or a push. A failed read or write is a transient pause before pending.sh is asked. This pass also carries
# older pull requests over: one approved earlier gets its status, and its label when its item is handled, with no
# new review, and one that carries owner-decision from the old procedure gets has-decision.
#
# review-ok is a commit status the runner posts, and nothing else does, with
# `gh api repos/Adamniels/Personal-platform/statuses/<sha>`, context `review-ok`: `success`, "approved by the
# reviewer", when the latest verdict is approve for that exact sha, and `pending`, "waiting for the reviewer",
# otherwise. Never `failure`: a changes verdict is still waiting. The ruleset requires it, so GitHub lets nobody
# merge a commit the reviewer has not approved. Besides the pass above, the runner posts it right after a review
# run. The merge gate is called only for a head found approved in the same loop, by the pass or by a review run
# that has just posted success on it, so review-ok is success on it before the gate is called.
#
# The labels. The runner owns four, and a pull request carries at most one of them:
#   in-review       an open pull request whose head has no approve. Set by the pass.
#   owner-merge     approved, waits for the owner, nothing to decide: he reads and merges.
#   owner-decision  approved, waits for the owner, and carries has-decision: he answers the decisions, by merging
#                   or by a comment.
#   owner-blocked   not mergeable as it is: he unblocks it.
# has-decision is the implementer's, set when it makes a decision the owner must answer. The runner never removes
# it, and adds it only to a pull request that carries owner-decision without it. So a label starting with `owner-`
# always means the reviewer approved the head, or the pull request is stuck, and it is the owner's turn.
# On a head with no approve the pass puts in-review and takes owner-merge and owner-decision off, so a new commit
# takes the owner label off and puts in-review back. owner-blocked stays on such a head, with no in-review beside
# it, only while it is still true: the latest verdict is for that head (the review rounds ran out on it), or the
# pull request is recorded in agent-logs/blocked-prs (the runner set its slice Blocked). On an approved head the
# pass leaves in-review and the owner labels to the turn of the review that approved it, or to the head's merge or
# owner item, which set them from the gate's reasons: owner-merge or owner-decision replaces in-review and
# owner-blocked, owner-blocked replaces the other three, and a pull request the gate merges gets no owner label,
# its in-review taken off after the merge (a failure there is only logged). A failed label write anywhere else is
# a transient pause.
#
# After a merge the runner is done with a pull request. One the gate merged, or answered with the single reason
# `pull request is already merged`, or that GitHub says is merged, is recorded in agent-logs/merged-prs. An item
# for a recorded pull request, or for one that is merged or closed when its item is picked, is dropped: a log line
# and a wait of at most a minute (never more than the poll interval) so a listing that lags cannot spin the loop,
# and no label, no mention, no pause and no count against the slice. The same holds after a claude run for a pull
# request item: when the pull request is merged or closed by then, the run is nothing to do and not a failure,
# whatever it left.
#
# Work items (one line from pending.sh) and what the runner does with each:
#   new <slice>, resume <slice>, fix <pr> <slice>
#                        claude -p "/pick-up-slice <slice>" --permission-mode auto --disallowedTools <patterns>,
#                        with a copy of gh-guard.sh first on PATH as `gh` and its path in AGENT_GH_GUARD (R25, R36).
#                        Before the run a dirty tree on the slice's branch is committed locally as "Checkpoint:
#                        interrupted run" (resume and fix; never pushed), and a dirty tree anywhere else is stashed,
#                        mentioned, and paused on (R16).
#   review <pr> <slice>  claude -p "Use the reviewer agent on pull request <pr>" --permission-mode auto, only from a
#                        clean tree on main (else an environment pause, R53). A review that leaves no verdict for the
#                        head of a pull request that is still open is a failed run (R32). After it, review-ok is
#                        posted on the head: success for an approve, pending otherwise (a failed write is a
#                        transient pause).
#                        The same turn: when the verdict is approve for the head as it is after the run, and
#                        review-ok was made success, the pull request is settled at once, before pending.sh is
#                        asked again. The head is recorded as approved for this loop and handled exactly as a
#                        merge item is, below, with the head and the labels read after the run, not the ones the
#                        item was picked with: merged, or handed to the owner with its label and mention, or
#                        owner-blocked, and a gate error or a failed label or mention write is a transient pause,
#                        never a count against the slice. One difference: the turn never waits the poll interval.
#                        With `ci-ok is pending` among the reasons it records nothing, labels nothing and
#                        returns, and the pull request's merge or owner item settles it once CI has reported;
#                        and when the mention for this head was made before, it sets the label and goes on.
#                        Nothing is settled when the run's own accounting paused or set the slice Blocked, or
#                        when the pull request was merged or closed meanwhile.
#   merge <pr> <slice>   merge-gate.sh <pr>, only for a head this loop found approved; any other head (it moved
#                        since pending.sh looked) waits up to a minute and is looked at again, with no gate call.
#                        Exit 0: merged, and recorded as merged. Exit 3 with the single reason `pull request is
#                        already merged`: dropped, as above. Exit 3 with `ci-ok is pending` among the reasons: wait
#                        the poll interval (R45), with one mention once the same head has waited 2 h (R50).
#                        Otherwise, only `risk is not low`, `touches protected paths` and `an owner label is set`:
#                        the PR is ready, and gets "ready to read and merge" and owner-merge, or, when it carries
#                        has-decision, "approved and waits for your decision" and owner-decision; any other
#                        reasons: a mention with them and owner-blocked. Exit 1 or another code: a transient pause.
#   cap <pr> <slice>     a mention that the review rounds ran out, and owner-blocked.
#   owner <pr> <slice>   a PR with has-decision or owner-blocked approved for its head: handled as merge is. The
#                        gate cannot merge it, and its reasons say whether it is ready or stuck. It is what settles
#                        a PR the review's own turn could not: ci-ok was pending, or the handover paused, or the
#                        PR was approved before the same turn existed. pending.sh offers
#                        it before resume and new, so the owner label and the mention do not wait for the queue,
#                        and no longer once the PR carries owner-merge or owner-decision without owner-blocked: it
#                        was handed over for this head, and the next loop goes on to other work with no wait. One
#                        carrying owner-blocked is offered last, every loop, and waits as below.
#   (nothing)            idle: one mention per empty queue, on the most recently merged slice (without one, a log
#                        line), then the poll interval.
# Mentions go on the slice issue. Every mention about a PR (ready, refused, cap, owner, ci-ok waiting) is made once per
# PR and head sha: agent-logs/owner-told holds "<pr> <sha>" lines (R42; the ci-ok wait uses "<pr> <sha>:ci", R52), and an item whose mention was already made
# waits the poll interval instead of looping (R43b). What still reaches that wait is chiefly an approved PR that
# stays owner-blocked, offered every loop; a PR handed over with owner-merge or owner-decision does not, since
# pending.sh no longer offers it. After every item a dirty tree on the slice's own branch left by
# its implementer run is checkpointed, dirt anywhere else after a claude run is stashed with a mention, and a clean
# tree is switched to main and fast-forwarded to refs/runner/main (a failure is an environment pause).
#
# Every claude run is classified (R13, R35) as ok, limit, transient, environment or task, and only task counts
# against the slice. A last line starting `ENVIRONMENT:` is environment; `Stopped:` or `Blocked:` is task; any other
# exit 0 is ok. A failed run is limit when its last 20 lines carry a usage limit message (`usage limit reached`,
# `limit reached|<epoch>`, `limit reached … resets`, `hit your limit`, `rate_limit_error`, `HTTP 429`, `status 429`),
# transient on a network or API error, and otherwise task when it lasted at least 120 s, transient when faster. The
# same item at the same PR head coming back after an ok run is task too (no progress; agent-logs/last-ok). Two task
# failures in a row set the slice Blocked, add owner-blocked to its PR and record "<pr> <sha>" in
# agent-logs/blocked-prs, with one mention: on
# the PR for a PR item (R54; that bot comment also advances the PR's last handled time, so pending.sh's owner-input
# rule does not fire again on an old comment), on the slice issue otherwise. A new head does not unblock it: pending.sh
# skips a Blocked slice's PR unless the owner commented since (R51), and the runner waits on any PR item of a
# recorded PR whose slice is Blocked (R47). The owner unblocks by commenting on the PR, whose fix item sets the slice
# back to In review and clears the record before its run, or by moving the slice off Blocked, which clears the record.
# A Block whose mention went out but whose board, label or record write failed is finished before any other item of
# that slice, so a merge, cap or owner item cannot slip past it (R55). Moving a slice off Blocked without commenting
# yields one extra review run, since pending.sh reads the Blocked notice as a bot reply (intended). An ok run resets
# the count.
# limit, transient and environment pause and then ask pending.sh again, changing nothing on the board (R14, R46):
# limit sleeps until the reset in the output plus 60 s (an epoch after `|`, or a `resets 3pm` style time), else
# 15 min doubling to 60 min, and mentions only once its episode passes 6 h or 10 limits in a row on one item;
# transient and environment sleep 1 min doubling to 30 min and mention once per episode past 30 min. Before every
# claude run the runner probes `gh api rate_limit`, `docker info` and free disk over 5 GB; a failed probe is an
# environment pause with no claude call (R15). When `docker info` fails, the runner first starts Docker once and
# waits for it to answer. pending.sh failing is a transient pause. Every external call runs
# under a timeout (claude AGENT_TIMEOUT_SECONDS, the others 60 to 300 s); a timed out call is a failed call.
# Sleeps go through AGENT_SLEEP in chunks of at most 30 s, checking the stop file between them.
#
# Environment (defaults in brackets):
#   AGENT_HOME                  [$HOME] holds the stop file .agent-stop, runner/run.sh and agent-logs/: runner.log,
#                               one <slice>-<YYYYmmdd-HHMMSS>.log per claude run, and the state files
#   AGENT_REPO                  [the repository holding this script] the clone the agents work in
#   AGENT_REMOTE                [https://github.com/Adamniels/Personal-platform.git] where main is fetched from
#   AGENT_SCRIPTS               [scripts/agent from refs/runner/main, each loop] a fixed directory holding
#                               pending.sh, merge-gate.sh, board.sh, verdict.sh and gh-guard.sh instead: a test override
#   AGENT_POLL_SECONDS          [600] idle, ci-ok pending and already-told wait
#   AGENT_TIMEOUT_SECONDS       [5400] a claude run is killed, with its process group, after this long
#   AGENT_CALL_TIMEOUT_SECONDS  [per call, 60 to 300] one timeout for every other external call: a test override
#   AGENT_SLEEP                 [sleep] the sleep command
#   AGENT_DOCKER_START          [open -a Docker] starts Docker when its probe fails; empty never starts it
#   AGENT_DOCKER_START_WAIT     [120] seconds to wait for a started Docker to answer
# claude, gh, docker, git, df, date and jq come from PATH.
# Exit codes: 0 stopped by the stop file, 1 cannot fetch main, copy the runner, or set up agent-logs or the
# repository, 2 already running (run.lock holds the PID of a live run.sh) or a bad environment value, 130 a signal.
set -uo pipefail

OWNER="Adamniels"
REPO_SLUG="Adamniels/Personal-platform"
BOT="mac-mini-agent"
LABEL_REVIEW="in-review"          # an open pull request whose head has no approve: in the review loop
LABEL_MERGE="owner-merge"         # approved, waits for the owner, nothing to decide: he reads and merges
LABEL_DECISION="owner-decision"   # approved, waits for the owner, and carries has-decision: he answers the decisions
LABEL_BLOCKED="owner-blocked"     # not mergeable as it is: the owner has to unblock it
LABEL_HAS="has-decision"          # the implementer's: it made a decision the owner must answer; never removed
STATUS="review-ok"                # the commit status the ruleset requires; only the runner posts it
SHORT_WAIT=60                     # the longest wait before looking again at an item that had nothing to do
SNAP="refs/runner/main"           # main as last fetched by the runner; everything it runs comes from here
MIN_TASK_SECONDS=120              # a failure faster than this is not the slice's fault (R13)
MIN_FREE_KB=$((5 * 1024 * 1024))
CHUNK=30                          # longest single sleep, so the stop file is seen quickly
MENTION_AFTER=1800                # a transient or environment episode mentions the owner once past this
LIMIT_MENTION_AFTER=21600         # a limit episode mentions once past this ...
LIMIT_MENTION_COUNT=10            # ... or after this many limits in a row on one item
CI_MENTION_AFTER=7200             # ci-ok pending this long on one head mentions once (R50)
LIMIT_RE='usage limit reached|limit reached\|[0-9]{10}|limit reached.{0,8}resets|hit your limit|rate_limit_error|http 429|status 429'
TRANSIENT_RE='connection error|econnreset|econnrefused|etimedout|enotfound|eai_again|socket hang up|fetch failed|network error|api error: ?5[0-9][0-9]|overloaded'

here="$(cd "$(dirname "$0")" && pwd)"
home="${AGENT_HOME:-$HOME}"
repo="${AGENT_REPO:-$(cd "$here/../.." && pwd)}"
remote="${AGENT_REMOTE:-https://github.com/$REPO_SLUG.git}"
scripts_override="${AGENT_SCRIPTS:-}"
poll="${AGENT_POLL_SECONDS:-600}"
timeout_secs="${AGENT_TIMEOUT_SECONDS:-5400}"
call_override="${AGENT_CALL_TIMEOUT_SECONDS:-}"
sleep_cmd="${AGENT_SLEEP:-sleep}"
docker_start="${AGENT_DOCKER_START-open -a Docker}"
docker_start_wait="${AGENT_DOCKER_START_WAIT:-120}"
logs="$home/agent-logs"
stop_file="$home/.agent-stop"
lock="$logs/run.lock"             # a directory holding pid
pause_file="$logs/pause"
owner_told="$logs/owner-told"
blocked_prs="$logs/blocked-prs"
ci_pending="$logs/ci-pending"
last_ok_file="$logs/last-ok"
open_prs="$logs/.open-prs"        # this loop's open pull requests by the bot: "<pr> <head sha> <labels>"
approved="$logs/.approved"        # this loop's approved heads, review-ok success on each: "<pr> <head sha>"
merged_prs="$logs/merged-prs"     # pull requests known to be merged, one number per line: the runner is done with them

[[ "$poll" =~ ^[0-9]+$ ]] || { echo "run.sh: AGENT_POLL_SECONDS must be a number: $poll" >&2; exit 2; }
[[ "$timeout_secs" =~ ^[1-9][0-9]*$ ]] || { echo "run.sh: AGENT_TIMEOUT_SECONDS must be a positive number: $timeout_secs" >&2; exit 2; }
[ -z "$call_override" ] || [[ "$call_override" =~ ^[1-9][0-9]*$ ]] \
  || { echo "run.sh: AGENT_CALL_TIMEOUT_SECONDS must be a positive number: $call_override" >&2; exit 2; }

# Git without the repository's hooks (R49): a branch can carry a hook, and the runner must not run it.
g() { git -c core.hooksPath=/dev/null -C "$repo" "$@"; }

# Start from main's run.sh as fetched into refs/runner/main, never the working tree's. The copy is moved into place,
# so a runner already reading an older copy keeps its own file. AGENT_RUNNER_COPY marks the copy and is not passed on to children.
if [ -z "${AGENT_RUNNER_COPY:-}" ]; then
  mkdir -p "$home/runner" || { echo "run.sh: cannot create $home/runner" >&2; exit 1; }
  g fetch -q "$remote" "+refs/heads/main:$SNAP" || { echo "run.sh: cannot fetch main from $remote; not starting" >&2; exit 1; }
  g show "$SNAP:scripts/agent/run.sh" > "$home/runner/run.sh.$$" && mv -f "$home/runner/run.sh.$$" "$home/runner/run.sh" \
    || { rm -f "$home/runner/run.sh.$$"; echo "run.sh: main has no readable scripts/agent/run.sh; not starting" >&2; exit 1; }
  AGENT_RUNNER_COPY=1 AGENT_REPO="$repo" exec bash "$home/runner/run.sh" "$@"
fi
unset AGENT_RUNNER_COPY

# Implementer runs refuse these Bash commands (R25, I5); settings.json and the gh guard cover the same ground.
# Review runs deliberately get no --disallowedTools: the reviewer writes the `<!-- verdict:` marker itself.
DENY=("Bash(*verdict: *)" "Bash(*<!--*)" "Bash(*merge-gate.sh*)"
      "Bash(*gh pr merge*)" "Bash(*gh pr *merge*)" "Bash(*gh * pr *merge*)" "Bash(*pulls/*/merge*)"
      "Bash(*mergePullRequest*)" "Bash(*enablePullRequestAutoMerge*)" "Bash(*--edit-last*)" "Bash(*--delete-last*)")
for m in PATCH DELETE patch delete; do
  DENY+=("Bash(*gh api *-X $m*)" "Bash(*gh api *-X$m*)" "Bash(*gh api *-X=$m*)"
         "Bash(*gh api *--method $m*)" "Bash(*gh api *--method=$m*)")
done

# --- Basics ------------------------------------------------------------------------------------------------------
now() { date +%s; }
log() { printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" | tee -a "$logs/runner.log" >&2; }
# log_lines <prefix> <file>: each line of a captured stderr, into runner.log.
log_lines() { local l; [ -s "$2" ] || return 0; while IFS= read -r l; do log "$1: $l"; done < "$2"; }
stopped() { [ -e "$stop_file" ]; }
# nap <seconds>: sleeps in chunks; returns 1 as soon as the stop file exists.
nap() {
  local left="$1" c
  while [ "$left" -gt 0 ]; do
    stopped && return 1
    c=$CHUNK; [ "$left" -lt "$c" ] && c="$left"
    "$sleep_cmd" "$c"
    left=$((left - c))
  done
  return 0
}
# short_wait: before looking again at an item that had nothing to do, so a listing that lags cannot spin the loop.
# Never longer than the poll interval, and not a pause: no episode, no backoff, no mention.
short_wait() { local w="$SHORT_WAIT"; [ "$poll" -lt "$w" ] && w="$poll"; nap "$w"; }
number() { [[ "${1:-}" =~ ^[0-9]+$ ]]; }
# Runner-made text in a mention: no mentions, no code spans, one line, short.
sanitize() { printf '%s' "$1" | tr -d '`@\r' | tr '\n' ' ' | cut -c1-200; }

# with_timeout <seconds> <stdout file> <stderr file, or empty for the stdout file> <command...>: runs the command in
# its own process group with stdin from /dev/null, and kills the whole group (TERM, then KILL after 10 s) when it
# outlives <seconds>. Returns the command's exit code, or 124 on a timeout.
child_pid="" watcher_pid=""
with_timeout() {
  local t="$1" out="$2" err="$3" fired="$logs/.timeout.$$" pid rc
  shift 3
  rm -f "$fired"
  set -m
  if [ -z "$err" ]; then "$@" < /dev/null > "$out" 2>&1 & else "$@" < /dev/null > "$out" 2> "$err" & fi
  pid=$!
  ( sleep "$t"; : > "$fired"; kill -TERM -- "-$pid" 2> /dev/null; sleep 10; kill -KILL -- "-$pid" 2> /dev/null ) \
    < /dev/null > /dev/null 2>&1 &
  watcher_pid=$!
  set +m
  child_pid="$pid"
  wait "$pid"; rc=$?
  child_pid=""
  kill -TERM -- "-$watcher_pid" 2> /dev/null; wait "$watcher_pid" 2> /dev/null; watcher_pid=""
  kill -TERM -- "-$pid" 2> /dev/null   # whatever the command left behind in its group
  if [ -e "$fired" ]; then rm -f "$fired"; return 124; fi
  return "$rc"
}
# call <seconds> <command...>: a bounded external call (I4). Its stdout is in $call_out, its stderr goes to
# runner.log, and a timeout is logged and returns 124.
call_out=""
call_name=""      # set by cgit, so a git call is logged by its verb
call() {
  local t="$1" name rc
  shift
  [ -n "$call_override" ] && t="$call_override"
  name="$1"; [ "$1" = bash ] && name="$(basename "${2:-bash}")"
  [ -n "$call_name" ] && name="$call_name"
  call_name=""
  with_timeout "$t" "$logs/.call.out" "$logs/.call.err" "$@"; rc=$?
  call_out="$(cat "$logs/.call.out" 2> /dev/null)"
  log_lines "$name" "$logs/.call.err"
  [ "$rc" = 124 ] && log "$name timed out after $t s"
  return "$rc"
}
cgit() { local t="$1"; shift; call_name="git $1"; call "$t" git -c core.hooksPath=/dev/null -C "$repo" "$@"; }

# --- GitHub --------------------------------------------------------------------------------------------------------
comment() { call 120 gh issue comment "$1" --repo "$REPO_SLUG" --body "$2"; }
# head_of <pr>: sets $head_sha, $head_state (OPEN, MERGED or CLOSED) and $head_labels (the PR's label names, comma
# separated), or fails.
head_sha="" head_state="" head_labels=""
head_of() {
  local sha rest state
  head_sha="" head_state="" head_labels=""
  call 120 gh pr view "$1" --repo "$REPO_SLUG" --json headRefOid,state,labels \
    --jq '.headRefOid, .state, ([.labels[].name] | join(","))' || return 1
  sha="${call_out%%$'\n'*}"
  [[ "$sha" =~ ^[0-9a-f]+$ ]] || { log "unreadable head of #$1: $call_out"; return 1; }
  rest=""; [ "$sha" = "$call_out" ] || rest="${call_out#*$'\n'}"
  state="${rest%%$'\n'*}"
  case "$state" in OPEN|MERGED|CLOSED) ;; *) log "unreadable state of #$1: $call_out"; return 1 ;; esac
  head_sha="$sha" head_state="$state"
  [ "$state" = "$rest" ] || head_labels="${rest#*$'\n'}"
}
# A pull request the runner knows is merged: the gate merged it, or said so, or GitHub did when it was read. The
# runner is done with it, whatever a listing that lags behind says.
was_merged() { grep -qxF "$1" "$merged_prs" 2> /dev/null; }
note_merged() { was_merged "$1" || echo "$1" >> "$merged_prs"; }
# gone <pr> <what it is> <item>: an item for a pull request that is merged or closed is nothing to do: a log line
# and a short wait, no label, no mention, no pause, no count against the slice.
gone() { log "#$1 is $2; nothing to do for: $3"; end_episode; short_wait; }
# review_ok <sha> <success|pending>: makes the commit's review-ok status that state. It reads the status the commit
# has and posts only when it differs, so a commit already right costs no write. Fails when the read or the post does.
review_ok() {
  status_of "$1" || return 1
  [ "$call_out" = "$2" ] && return 0
  post_status "$1" "$2"
}
# status_of <sha>: the commit's review-ok state in $call_out, or "none".
status_of() {
  call 120 gh api "repos/$REPO_SLUG/commits/$1/status?per_page=100" \
    --jq "[.statuses[] | select(.context == \"$STATUS\") | .state] | first // \"none\""
}
post_status() { # sha state
  local desc="waiting for the reviewer"
  [ "$2" = success ] && desc="approved by the reviewer"
  call 120 gh api "repos/$REPO_SLUG/statuses/$1" --silent -f "state=$2" -f "context=$STATUS" -f "description=$desc" || return 1
  log "$STATUS is $2 on $1"
}
# make_true: before pending.sh is asked, the labels and review-ok are made true for every open pull request by the
# bot, and the heads found approved are kept in $approved for this loop. A pull request known to be merged is
# skipped, since a listing can lag behind a merge. Returns 1, after a transient pause, when a read or a write failed.
make_true() {
  local pr sha labels
  call 120 gh pr list --repo "$REPO_SLUG" --state open --author "$BOT" --limit 100 --json number,headRefOid,labels \
    --jq '.[] | "\(.number) \(.headRefOid) \([.labels[].name] | join(","))"' \
    || { pause transient "could not list the open pull requests" ""; return 1; }
  printf '%s\n' "$call_out" > "$open_prs"
  : > "$approved"
  while read -r pr sha labels <&3; do
    [ -n "$pr" ] || continue
    number "$pr" && [[ "$sha" =~ ^[0-9a-f]+$ ]] || { pause transient "unreadable open pull request line: $pr $sha" ""; return 1; }
    was_merged "$pr" && continue
    true_pr "$pr" "$sha" "$labels" || { pause transient "could not make #$pr true" ""; return 1; }
  done 3< "$open_prs"
  return 0
}
# true_pr <pr> <head sha> <labels>: one pull request of make_true. From the listing, the verdict and the commit's
# status it works out what would change. Nothing: no write, and no further read. Something: the pull request itself
# is read first, and a write is made only to one that is open at the listed head; a merged or closed one, or one
# whose head moved, is left alone (the next loop reads the new head).
true_pr() {
  local pr="$1" sha="$2" vk vs want=pending ok=0 at_head=0 have
  call 120 bash "$scripts/verdict.sh" "$pr" || return 1
  read -r vk vs _ <<<"$call_out"
  [ "$vs" = "$sha" ] && at_head=1
  [ "$vk" = approve ] && [ "$at_head" = 1 ] && { want=success; ok=1; }
  status_of "$sha" || return 1
  have="$call_out"
  cur_labels="$3"
  loop_labels "$pr" "$ok" "$at_head"
  if [ "$have" != "$want" ] || [ "${#label_ops[@]}" -gt 0 ]; then
    head_of "$pr" || return 1
    if [ "$head_state" != OPEN ]; then
      [ "$head_state" = MERGED ] && note_merged "$pr"
      log "#$pr is listed as open but is $(tr '[:upper:]' '[:lower:]' <<<"$head_state"); nothing to make true"; return 0
    fi
    if [ "$head_sha" != "$sha" ]; then log "#$pr moved from $sha to $head_sha since it was listed; the next loop reads it"; return 0; fi
    [ "$have" = "$want" ] || post_status "$sha" "$want" || return 1
    cur_labels="$head_labels"
    loop_labels "$pr" "$ok" "$at_head"
    if [ "${#label_ops[@]}" -gt 0 ]; then
      call 120 gh pr edit "$pr" --repo "$REPO_SLUG" "${label_ops[@]}" || return 1
      log "labels of #$pr made true: ${label_ops[*]}"
    fi
  fi
  [ "$ok" = 1 ] && echo "$pr $sha" >> "$approved"
  return 0
}
approved_now() { grep -qxF "$1 $2" "$approved" 2> /dev/null; }
# The labels. $cur_labels is what the PR carried when it was last read. in-review and the three owner labels are
# the runner's, and a PR carries at most one of the four; has-decision is the implementer's, and the runner adds it
# only where an older pull request carries owner-decision without it, and never removes it.
cur_labels=""
has_label() { case ",$cur_labels," in *",$1,"*) return 0 ;; esac; return 1; }
# label_changes <labels to add, space separated, or ""> <label to remove>...: sets $label_ops to the `gh pr edit`
# flags that would change $cur_labels; empty when the labels are already right.
label_ops=()
label_changes() {
  local adds="$1" l
  shift
  label_ops=()
  for l in $adds; do has_label "$l" || label_ops+=(--add-label "$l"); done
  for l in "$@"; do has_label "$l" && label_ops+=(--remove-label "$l"); done
  return 0
}
# relabel <pr> <labels to add, or ""> <label to remove>...: one `gh pr edit`, skipped when nothing would change.
relabel() {
  local pr="$1"
  shift
  label_changes "$@"
  [ "${#label_ops[@]}" -gt 0 ] || return 0
  call 120 gh pr edit "$pr" --repo "$REPO_SLUG" "${label_ops[@]}"
}
# loop_labels <pr> <1 when the head is approved> <1 when the verdict is for the head>: sets $label_ops to what
# make_true changes on $cur_labels.
#   owner-decision without has-decision is what the old procedure left, set by the implementer: has-decision is
#   added, so the decision is kept whatever becomes of owner-decision.
#   An approved head is left as it is: the turn of the review that approved it, or its merge or owner item, sets
#   its label from the gate's reasons.
#   A head with no approve carries in-review and no owner label, so a new commit takes owner-merge and
#   owner-decision off. owner-blocked alone can stay, without in-review, while it is still true: the verdict is for
#   this head (the review rounds ran out on it), or the runner set the slice Blocked and still records the PR.
loop_labels() {
  local adds=""
  if has_label "$LABEL_DECISION" && ! has_label "$LABEL_HAS"; then adds="$LABEL_HAS"; fi
  if [ "$2" = 1 ]; then label_changes "$adds"
  elif has_label "$LABEL_BLOCKED" && { [ "$3" = 1 ] || pr_recorded "$1"; }; then
    label_changes "$adds" "$LABEL_MERGE" "$LABEL_DECISION" "$LABEL_REVIEW"
  else
    label_changes "$adds $LABEL_REVIEW" "$LABEL_MERGE" "$LABEL_DECISION" "$LABEL_BLOCKED"
  fi
}
# label_blocked <pr>: the PR cannot be merged as it is. owner-blocked replaces whichever of the other three it had.
label_blocked() { relabel "$1" "$LABEL_BLOCKED" "$LABEL_MERGE" "$LABEL_DECISION" "$LABEL_REVIEW"; }
# label_ready <pr>: approved, and nothing but the owner stands between the PR and main. With has-decision it gets
# owner-decision, never owner-merge; any other gets owner-merge. Either one replaces in-review and owner-blocked.
label_ready() {
  if has_label "$LABEL_HAS"; then relabel "$1" "$LABEL_DECISION" "$LABEL_BLOCKED" "$LABEL_MERGE" "$LABEL_REVIEW"
  else relabel "$1" "$LABEL_MERGE" "$LABEL_BLOCKED" "$LABEL_DECISION" "$LABEL_REVIEW"; fi
}
# escalate <key> <issue> <body> <action...>: mentions once, then runs the action; the marker in agent-logs/mentioned
# keeps a retried action from mentioning again. Returns 1 when the mention or the action failed.
escalate() {
  local mark="$logs/mentioned/$1" issue="$2" body="$3"
  shift 3
  if [ ! -e "$mark" ]; then
    comment "$issue" "$body" || { log "could not comment on #$issue"; return 1; }
    : > "$mark"
  fi
  "$@" || { log "the action after the mention on #$issue failed: $*"; return 1; }
  rm -f "$mark"
}
told() { grep -qxF "$1 $2" "$owner_told" 2> /dev/null; }
# tell_owner <pr> <sha> <slice> <body>: mentions once per PR and head sha (R42); $already_told says it was done before.
already_told=0
tell_owner() {
  already_told=0
  if told "$1" "$2"; then log "the owner was already told about #$1 at $2"; already_told=1; return 0; fi
  comment "$3" "$4" || { log "could not comment on #$3"; return 1; }
  echo "$1 $2" >> "$owner_told"
}
# hand_over <pr> <sha> <slice> <body> <label_ready|label_blocked> [turn]: tells the owner once for this head, then
# sets the label; when the mention was made before, waits the poll interval, so a repeated item cannot spin (R43b).
# With `turn`, in the turn of the review that approved the head, it never waits: no item repeats there.
hand_over() {
  tell_owner "$1" "$2" "$3" "$4" && "$5" "$1" || return 1
  [ "$already_told" = 1 ] && [ "${6:-}" != turn ] && nap "$poll"
  return 0
}
# merged_slice: sets $merged to the slice of the most recently merged bot PR, or empty.
merged=""
merged_slice() {
  merged=""
  call 120 gh pr list --repo "$REPO_SLUG" --state merged --author "$BOT" --limit 30 \
    --json number,mergedAt,headRefName,closingIssuesReferences || return 1
  merged="$(jq -r 'sort_by(.mergedAt) | last | if . == null then empty else
    ((.closingIssuesReferences[0].number // empty | tostring), ((.headRefName // "") | capture("^(?<n>[0-9]+)-").n)?)
    end' <<<"$call_out" 2> /dev/null | head -n1)"
}

# --- Failures and pauses -----------------------------------------------------------------------------------------
failures() { local n; n="$(cat "$logs/failures/$1" 2> /dev/null)"; number "$n" && echo "$n" || echo 0; }
reset_failures() { rm -f "$logs/failures/$1"; }
blocked_pr() { grep -qxF "$1 $2" "$blocked_prs" 2> /dev/null; }

do_block() { # slice pr sha
  call 120 bash "$scripts/board.sh" status "$1" Blocked || return 1
  [ "$2" = - ] && return 0
  label_blocked "$2" || return 1
  blocked_pr "$2" "$3" || echo "$2 $3" >> "$blocked_prs"
}
# block_slice: the mention goes on the PR for a PR item (R54), so it counts as the bot's reply there; runner-made text
# and the log path only, first line never a marker.
block_slice() { # slice pr sha item short logfile
  local n see="" where="$1"
  n="$(failures "$1")"
  [ -n "$6" ] && see=" See $6 on the Mac mini."
  [ "$2" = - ] || where="$2"
  if escalate "blocked-$1" "$where" "@$OWNER slice #$1 is Blocked: $n runs in a row failed on it, the last one $4 ($5).$see Comment on this PR or move the slice off Blocked to run it again." do_block "$1" "$2" "$3"; then
    log "slice $1 set Blocked after $n failures"
    reset_failures "$1"
  else
    pause transient "could not set slice $1 Blocked" "$1"
  fi
}
count_failure() { # slice pr sha item short logfile
  local n=$(( $(failures "$1") + 1 ))
  echo "$n" > "$logs/failures/$1"
  log "task failure $n for slice $1 ($4): $5"
  [ "$n" -ge 2 ] && block_slice "$@"
  return 0
}

# reset_wait <log>: sets $reset_w to the seconds until the usage limit's reset plus 60 s, from an epoch after `|` or
# a `resets 3pm`, `resets at 15:00` style time (the next one, local time). Fails when the log names no usable reset.
reset_w=""
reset_wait() {
  local text epoch target t H M S h m ap re line midnight w
  reset_w=""
  text="$(grep -v '^[[:space:]]*$' "$1" 2> /dev/null | tail -n 20)"
  read -r t H M S <<<"$(date '+%s %H %M %S')"
  number "$t" || return 1
  epoch="$(grep -Eo '\|[0-9]{10}' <<<"$text" | tail -n1)"; epoch="${epoch#|}"
  if [ -n "$epoch" ]; then
    target="$epoch"
  else
    re='[Rr]esets( at)? ([0-9]{1,2})(:([0-9]{2}))? ?([AaPp][Mm])?'
    line="$(grep -Eo "$re" <<<"$text" | tail -n1)"
    [[ "$line" =~ $re ]] || return 1
    h=$((10#${BASH_REMATCH[2]})); m=$((10#${BASH_REMATCH[4]:-0})); ap="$(tr '[:upper:]' '[:lower:]' <<<"${BASH_REMATCH[5]}")"
    case "$ap" in am) [ "$h" = 12 ] && h=0 ;; pm) [ "$h" -lt 12 ] && h=$((h + 12)) ;; esac
    [ "$h" -lt 24 ] && [ "$m" -lt 60 ] || return 1
    midnight=$(( t - (10#$H * 3600 + 10#$M * 60 + 10#$S) ))
    target=$(( midnight + h * 3600 + m * 60 ))
    [ "$target" -le "$t" ] && target=$((target + 86400))
  fi
  w=$(( target + 60 - t ))
  [ "$w" -lt 60 ] && w=60
  [ "$w" -le $((8 * 86400)) ] || return 1
  reset_w="$w"
}

# mention_pause <slice> <text>: one mention for the pause episode, on the slice, the last slice, or the most recently
# merged one. Returns 0 when the episode needs no further mention (made, or nowhere to make it).
last_slice=""
mention_pause() {
  local target="${1:-$last_slice}"
  [ -n "$target" ] || { merged_slice; target="$merged"; }
  if [ -z "$target" ]; then log "no slice to mention the owner on about the pause"; return 0; fi
  comment "$target" "@$OWNER $2" && { log "mentioned the owner on #$target about the pause"; return 0; }
  return 1
}

# pause <limit|transient|environment> <reason> <slice or empty> [<claude log>]: no count, no board change; sleeps by
# the episode's backoff. State: "<start> <limit backoff> <other backoff> <mentioned> <limits in a row> <item>".
paused=0
cur_item="-"
pause() {
  local kind="$1" reason="$2" slice="$3" runlog="${4:-}" t start ln on men lc litem w mins what
  paused=1
  t="$(now)"
  read -r start ln on men lc litem 2> /dev/null < "$pause_file"
  number "${start:-}" && number "${ln:-}" && number "${on:-}" && number "${men:-}" && number "${lc:-}" && [ -n "${litem:-}" ] \
    || { start="$t" ln=0 on=0 men=0 lc=0 litem=-; }
  mins=$(( (t - start) / 60 ))
  what="$(sanitize "$reason")"; [ -n "$runlog" ] && what="$kind, see $runlog"
  if [ "$kind" = limit ]; then
    if [ "$litem" = "$cur_item" ]; then lc=$((lc + 1)); else lc=1; litem="$cur_item"; fi
    w=""; [ -n "$runlog" ] && reset_wait "$runlog" && w="$reset_w"
    if [ -z "$w" ]; then w=$((900 << ln)); [ "$w" -gt 3600 ] && w=3600; [ "$ln" -lt 3 ] && ln=$((ln + 1)); fi
    if [ "$men" = 0 ] && { [ $((t - start)) -gt "$LIMIT_MENTION_AFTER" ] || [ "$lc" -ge "$LIMIT_MENTION_COUNT" ]; }; then
      mention_pause "$slice" "the runner has been held by usage limits for $mins min ($lc in a row on $cur_item); it keeps retrying, see $runlog." && men=1
    fi
  else
    w=$((60 << on)); [ "$w" -gt 1800 ] && w=1800; [ "$on" -lt 5 ] && on=$((on + 1))
    if [ "$men" = 0 ] && [ $((t - start)) -gt "$MENTION_AFTER" ]; then
      mention_pause "$slice" "the runner has been paused for $mins min: $what. It retries on its own and has changed nothing on the board." && men=1
    fi
  fi
  echo "$start $ln $on $men $lc $litem" > "$pause_file"
  log "pausing ($kind: $reason) for $w s"
  nap "$w"
}
end_episode() { [ -e "$pause_file" ] && { log "the pause episode is over"; rm -f "$pause_file"; }; return 0; }

# --- Claude runs -------------------------------------------------------------------------------------------------
# classify_run <exit> <seconds> <log>: prints ok, limit, transient, environment or task (R13, R35, I3).
classify_run() {
  local code="$1" secs="$2" text last
  text="$(grep -v '^[[:space:]]*$' "$3" 2> /dev/null | tail -n 20)"
  last="$(tail -n1 <<<"$text")"
  case "$last" in
    ENVIRONMENT:*) echo environment; return ;;
    Stopped:*|Blocked:*) echo task; return ;;
  esac
  if [ "$code" = 0 ]; then echo ok; return; fi
  if grep -Eiq "$LIMIT_RE" <<<"$text"; then echo limit; return; fi
  if grep -Eiq "$TRANSIENT_RE" <<<"$text"; then echo transient; return; fi
  if [ "$secs" -ge "$MIN_TASK_SECONDS" ]; then echo task; else echo transient; fi
}

# Docker Desktop quits itself to install an update, and on a Mac nobody is signed in at it does not always start
# again. So the runner starts it once per failed probe and waits up to AGENT_DOCKER_START_WAIT s for it to answer.
# AGENT_DOCKER_START is word-split on purpose (a command and its arguments); set it empty to never start Docker.
start_docker() {
  local waited=0
  [ -n "$docker_start" ] || return 1
  log "docker is down; starting it with: $docker_start"
  # shellcheck disable=SC2086
  call 30 $docker_start || { log "could not start docker"; return 1; }
  while [ "$waited" -lt "$docker_start_wait" ]; do
    nap 10 || return 1
    waited=$((waited + 10))
    if call 60 docker info; then log "docker is back after $waited s"; return 0; fi
  done
  log "docker did not answer within $docker_start_wait s"
  return 1
}

probe_reason=""
probe() {
  local free
  call 120 gh api rate_limit || { probe_reason="gh api rate_limit failed"; return 1; }
  call 60 docker info || start_docker || { probe_reason="docker info failed"; return 1; }
  call 60 df -Pk "$repo" || { probe_reason="df failed"; return 1; }
  free="$(awk 'NR == 2 { print $4 }' <<<"$call_out")"
  if ! number "$free" || [ "$free" -le "$MIN_FREE_KB" ]; then probe_reason="free disk is ${free:-unknown} KB, under 5 GB"; return 1; fi
  return 0
}

# checkpoint <branch> <why>: commits everything, unverified and unpushed, as R16's checkpoint.
checkpoint() {
  cgit 60 add -A && cgit 60 commit -q --no-verify -m "Checkpoint: interrupted run" \
    -m "The runner found uncommitted changes on $1 at $(date '+%Y-%m-%d %H:%M:%S'), $2. They are committed as they were, unverified and not pushed; pick-up-slice inspects, finishes and gates them." \
    && log "committed a checkpoint on $1 ($2)"
}
# stash_foreign <slice> <branch> <when>: stashes dirt that is not the slice's, with one mention.
stash_foreign() {
  local ts; ts="$(date '+%Y-%m-%d %H:%M:%S')"
  cgit 60 stash push -u -q -m "runner: interrupted $1 $ts" || return 1
  log "stashed uncommitted changes on $2 $3"
  comment "$1" "@$OWNER the runner found uncommitted changes on $(sanitize "$2") $3 and stashed them as \"runner: interrupted $1 $ts\" (git stash list on the Mac mini). This should not happen; nothing was lost." \
    || log "could not mention the stash on #$1"
  return 0
}
current_branch() { cgit 60 branch --show-current; br="${call_out:-a detached HEAD}"; }

# prepare_tree <kind> <slice>: 0 when the tree is clean or checkpointed; otherwise it pauses and returns 1.
br=""
prepare_tree() {
  local kind="$1" slice="$2"
  cgit 60 status --porcelain || { pause environment "git status failed" "$slice"; return 1; }
  [ -z "$call_out" ] && return 0
  current_branch
  if [ "$kind" != new ] && [[ "$br" == "$slice-"* ]]; then
    checkpoint "$br" "before a $kind run, left by an interrupted run" && return 0
    pause environment "could not commit the checkpoint on $br" "$slice"; return 1
  fi
  stash_foreign "$slice" "$br" "before a $kind run of slice #$slice" \
    || { pause environment "dirty tree on $br, and the stash failed" "$slice"; return 1; }
  pause environment "dirty tree on $br, stashed" "$slice"
  return 1
}

claude_item() { # kind pr slice; the PR's head is $cur_sha
  local kind="$1" pr="$2" slice="$3" sha="$cur_sha" item logf n start rc secs cls short last key vk vs want what status_failed=""
  local settle_sha="" settle_labels=""
  item="$kind $slice"; [ "$pr" = - ] || item="$kind $pr $slice"
  probe || { pause environment "$probe_reason" "$slice"; return; }
  if [ "$kind" = review ]; then
    # R53: the reviewer reads main's agent and settings from a clean tree on main.
    cgit 60 status --porcelain || { pause environment "git status failed" "$slice"; return; }
    [ -z "$call_out" ] || { pause environment "the tree is dirty before a review" "$slice"; return; }
    current_branch
    [ "$br" = main ] || { pause environment "the tree is on $br, not main, before a review" "$slice"; return; }
  else
    cp "$scripts/gh-guard.sh" "$guard_dir/gh" 2>> "$logs/runner.log" && chmod +x "$guard_dir/gh" \
      || { pause environment "no gh-guard.sh in $scripts" "$slice"; return; }
    prepare_tree "$kind" "$slice" || return
  fi

  logf="$logs/$slice-$(date '+%Y%m%d-%H%M%S').log"; n=1
  while [ -e "$logf" ]; do n=$((n + 1)); logf="$logs/$slice-$(date '+%Y%m%d-%H%M%S')-$n.log"; done
  log "running claude for $item, log $logf"
  start="$(now)"
  if [ "$kind" = review ]; then
    with_timeout "$timeout_secs" "$logf" "" claude -p "Use the reviewer agent on pull request $pr" --permission-mode auto
  else
    with_timeout "$timeout_secs" "$logf" "" env PATH="$guard_dir:$PATH" AGENT_GH_GUARD="$guard_dir/gh" \
      claude -p "/pick-up-slice $slice" --permission-mode auto --disallowedTools "${DENY[@]}"
  fi
  rc=$?
  secs=$(( $(now) - start ))
  cls="$(classify_run "$rc" "$secs" "$logf")"
  last="$(grep -v '^[[:space:]]*$' "$logf" 2> /dev/null | tail -n1)"
  short="exit $rc after $secs s"; [ "$rc" = 124 ] && short="timed out after $secs s"

  # After a run for a pull request, one that is no longer open is nothing to do: the owner merged or closed it
  # meanwhile. A reviewer rightly leaves no verdict on such a pull request, so the run is not a failure.
  if [ "$pr" != - ] && { [ "$cls" = ok ] || [ "$cls" = task ]; }; then
    if head_of "$pr"; then
      if [ "$head_state" != OPEN ]; then
        [ "$head_state" = MERGED ] && note_merged "$pr"
        what="review"; [ "$kind" = review ] || what="$kind run"
        log "the $what of #$pr found it $(tr '[:upper:]' '[:lower:]' <<<"$head_state"); nothing to do ($short)"
        return
      fi
    elif [ "$kind" = review ] && [ "$cls" = ok ]; then
      pause transient "could not read the head of #$pr" "$slice"; return
    fi
  fi
  if [ "$kind" = review ] && [ "$cls" = ok ]; then
    call 120 bash "$scripts/verdict.sh" "$pr" || { pause transient "verdict.sh failed after the review of #$pr" "$slice"; return; }
    read -r vk vs _ <<<"$call_out"
    if [ "$vk" = none ] || [ "$vs" != "$head_sha" ]; then
      log "the review of #$pr left no verdict for head $head_sha"
      cls="$(classify_run 1 "$secs" "$logf")"
      short="the review left no verdict for head $head_sha"
    fi
    # review-ok right after the review: success on an approved head, pending on any other.
    want=pending; [ "$vk" = approve ] && [ "$vs" = "$head_sha" ] && want=success
    if ! review_ok "$head_sha" "$want"; then status_failed="$head_sha"
    elif [ "$want" = success ]; then
      # The head is approved and its review-ok is success: recorded for this loop as make_true records it, and
      # settled below, with the head and the labels just read, not the ones the item was picked with.
      approved_now "$pr" "$head_sha" || echo "$pr $head_sha" >> "$approved"
      settle_sha="$head_sha" settle_labels="$head_labels"
    fi
  fi
  log "claude for $item: $cls ($short; last line: $last)"

  case "$cls" in
    ok)
      key="$item $sha"
      if [ "$(cat "$last_ok_file" 2> /dev/null)" = "$key" ]; then
        count_failure "$slice" "$pr" "$sha" "$item" "no progress since the last ok run" "$logf"
      else
        reset_failures "$slice"
      fi
      echo "$key" > "$last_ok_file" ;;
    task) rm -f "$last_ok_file"; count_failure "$slice" "$pr" "$sha" "$item" "$short" "$logf" ;;
    *) pause "$cls" "$short" "$slice" "$logf" ;;
  esac
  # The run is accounted for above; the status is written again by the next loop's make_true.
  [ -z "$status_failed" ] || [ "$paused" = 1 ] || pause transient "could not set $STATUS on $status_failed" "$slice"
  # The same turn: an approved head is settled at once, as its merge or owner item would settle it, before
  # pending.sh is asked for anything else. Not when the run's accounting paused, set the slice Blocked or left a
  # Block half done: pending.sh offers nothing but owner input for a Blocked slice.
  if [ -n "$settle_sha" ] && [ "$paused" = 0 ] && [ "$(failures "$slice")" -lt 2 ] && ! pr_recorded "$pr"; then
    cur_sha="$settle_sha" cur_labels="$settle_labels"
    merge_item "$pr" "$slice" turn
  fi
}

# pr_gate <kind> <pr> <slice>: R47, R51 and R54 on a PR item. The board decides: a fix item (owner input) for a
# Blocked slice sets it back to In review and clears the PR's record; any other item for a Blocked slice whose PR is
# recorded, at any head, waits; a record whose slice is no longer Blocked (the owner moved it) is cleared. Returns 1
# when the item is not to run (it napped or paused).
pr_recorded() { grep -q "^$1 " "$blocked_prs" 2> /dev/null; }
clear_blocked() { [ -f "$blocked_prs" ] && { grep -v "^$1 " "$blocked_prs" > "$blocked_prs.new"; mv -f "$blocked_prs.new" "$blocked_prs"; }; return 0; }
pr_gate() {
  local kind="$1" pr="$2" slice="$3"
  [ "$kind" = fix ] || pr_recorded "$pr" || return 0
  call 120 bash "$scripts/board.sh" get "$slice" || { pause transient "could not read the status of slice $slice" "$slice"; return 1; }
  if [ "$call_out" = Blocked ]; then
    if [ "$kind" = fix ]; then
      call 120 bash "$scripts/board.sh" status "$slice" "In review" || { pause transient "could not move slice $slice to In review" "$slice"; return 1; }
      clear_blocked "$pr"; reset_failures "$slice"
      log "owner input on #$pr: slice $slice is In review again"
      return 0
    fi
    log "#$pr is Blocked; waiting for the owner"; end_episode; nap "$poll"; return 1
  fi
  if pr_recorded "$pr"; then
    clear_blocked "$pr"; rm -f "$logs/mentioned/blocked-$slice"; reset_failures "$slice"
    log "slice $slice is no longer Blocked; cleared #$pr's record, mention marker and failure count"
  fi
  return 0
}

# --- Merge, cap, owner, idle -------------------------------------------------------------------------------------
# ci_wait <pr> <sha> <slice>: notes when ci-ok was first seen pending on this head; once 2 h ago, one mention (R50),
# told under its own key `<sha>:ci`, so it never suppresses the later ready or refused mention (R52).
ci_wait() {
  local first t
  t="$(now)"
  first="$(awk -v k="$1 $2" '$1 " " $2 == k { print $3; exit }' "$ci_pending" 2> /dev/null)"
  if ! number "$first"; then
    { grep -v "^$1 " "$ci_pending" 2> /dev/null; echo "$1 $2 $t"; } > "$ci_pending.new" && mv -f "$ci_pending.new" "$ci_pending"
    first="$t"
  fi
  if [ $((t - first)) -ge "$CI_MENTION_AFTER" ] && ! told "$1" "$2:ci"; then
    tell_owner "$1" "$2:ci" "$3" "@$OWNER ci-ok has been pending on PR #$1 for $(( (t - first) / 60 )) min at head $2. The runner keeps waiting for it." \
      || log "could not mention the ci-ok wait on #$3"
  fi
}
ci_clear() { [ -f "$ci_pending" ] && { grep -v "^$1 " "$ci_pending" > "$ci_pending.new"; mv -f "$ci_pending.new" "$ci_pending"; }; return 0; }

# merge_item <pr> <slice> [turn]: a merge item and an owner item alike. For an owner item the gate cannot merge, since the
# PR carries has-decision or owner-blocked, but its reasons say whether the PR is ready for the owner or stuck, which the label
# alone does not: an approved PR whose ci-ok failed is not "ready to read and merge".
# With `turn` it is called by claude_item, in the turn of the review that approved the head, and differs in one
# thing: it never waits the poll interval. With ci-ok pending it records nothing and returns, and the PR's merge or
# owner item settles it once CI has reported.
merge_item() { # pr slice [turn]; head $cur_sha, labels $cur_labels
  local pr="$1" slice="$2" turn="${3:-}" out rc reasons joined r owner_only=1 decision
  # Only a head this loop's make_true found approved reaches the gate, so its review-ok is success before the gate
  # is called. Any other head moved after pending.sh looked: nothing is wrong with it, and the next loop reads it.
  if ! approved_now "$pr" "$cur_sha"; then
    log "#$pr has no approve for its head $cur_sha in this loop; looking again"; short_wait; return
  fi
  call 300 bash "$scripts/merge-gate.sh" "$pr"; rc=$?
  out="$call_out"
  case "$rc" in
    0)
      # Merged, and the runner is done with it: whatever a listing says afterwards, no label and no mention.
      log "merged #$pr: $out"; note_merged "$pr"; reset_failures "$slice"; ci_clear "$pr"
      # in-review comes off when it merges. Nothing reads a merged pull request's labels, so a failure is only logged.
      relabel "$pr" "" "$LABEL_REVIEW" || log "could not take $LABEL_REVIEW off the merged #$pr" ;;
    3)
      reasons="$(grep -v '^[[:space:]]*$' <<<"$out")"
      if [ "$reasons" = "pull request is already merged" ]; then
        log "#$pr is already merged; nothing to do"; note_merged "$pr"; ci_clear "$pr"; end_episode; short_wait; return
      fi
      if grep -qxF "ci-ok is pending" <<<"$reasons"; then
        if [ "$turn" = turn ]; then
          log "#$pr is approved and waits for ci-ok ($(tr '\n' ',' <<<"$reasons")); its item settles it"; return
        fi
        log "#$pr waits for ci-ok ($(tr '\n' ',' <<<"$reasons"))"; ci_wait "$pr" "$cur_sha" "$slice"; nap "$poll"; return
      fi
      ci_clear "$pr"
      [ -n "$reasons" ] || owner_only=0
      while IFS= read -r r; do
        case "$r" in "risk is not low"|"touches protected paths"|"an owner label is set") ;; *) owner_only=0 ;; esac
      done <<<"$reasons"
      joined="$(awk 'NR > 1 { printf ", " } { printf "%s", $0 }' <<<"${reasons:-no reasons printed}")"
      log "the merge gate refused #$pr: $joined"
      decision=0; has_label "$LABEL_HAS" && decision=1
      if [ "$owner_only" = 1 ] && [ "$decision" = 1 ]; then
        hand_over "$pr" "$cur_sha" "$slice" "@$OWNER PR #$pr is approved and waits for your decision: read \"Decisions the agent made\" in its body and \"For the owner\" in the review, then merge it or comment on it to change something ($joined)." label_ready "$turn" \
          || pause transient "could not hand #$pr to the owner" "$slice"
      elif [ "$owner_only" = 1 ]; then
        hand_over "$pr" "$cur_sha" "$slice" "@$OWNER PR #$pr is ready for you to read and merge: the merge gate leaves it to the owner ($joined)." label_ready "$turn" \
          || pause transient "could not hand #$pr to the owner" "$slice"
      else
        hand_over "$pr" "$cur_sha" "$slice" "@$OWNER PR #$pr needs you, do not merge it as it is: the merge gate refused it ($joined)." label_blocked "$turn" \
          || pause transient "could not hand #$pr to the owner" "$slice"
      fi ;;
    *) pause transient "merge-gate.sh exited $rc for #$pr" "$slice" ;;
  esac
}

cap_item() { # pr slice
  hand_over "$1" "$cur_sha" "$2" "@$OWNER PR #$1 has used up its review rounds, do not merge it as it is: the reviewer asked for changes a fourth time, so it waits for you." label_blocked \
    || pause transient "could not hand #$1 to the owner" "$2"
}

idle() {
  if [ ! -e "$logs/idle" ]; then
    merged_slice
    if [ -z "$merged" ]; then
      log "idle: the queue is empty, and no merged slice to mention the owner on"; : > "$logs/idle"
    elif comment "$merged" "@$OWNER the runner's queue is empty: no pull request waits on it and no slice is Ready. It looks again every $poll s."; then
      log "idle: the queue is empty, mentioned the owner on #$merged"; : > "$logs/idle"
    else
      log "idle: could not mention the owner on #$merged"
    fi
  fi
  nap "$poll"
}

# back_to_main <kind> <slice>: after an item, dirt from the slice's own implementer run is checkpointed on its branch
# and dirt elsewhere after a claude run is stashed (I6); after other items a dirty tree is left for the next run's
# R16 check. A clean tree goes to main at refs/runner/main.
back_to_main() {
  local kind="$1" slice="$2"
  cgit 60 status --porcelain || { pause environment "git status failed" "$slice"; return; }
  if [ -n "$call_out" ]; then
    current_branch
    case "$kind" in
      new|resume|fix)
        if [[ "$br" == "$slice-"* ]]; then
          checkpoint "$br" "after a $kind run of slice #$slice" || { pause environment "could not commit the checkpoint on $br" "$slice"; return; }
        else
          stash_foreign "$slice" "$br" "after a $kind run of slice #$slice" || { pause environment "dirty tree on $br, and the stash failed" "$slice"; return; }
        fi ;;
      review)
        stash_foreign "$slice" "$br" "after a review of slice #$slice" || { pause environment "dirty tree on $br, and the stash failed" "$slice"; return; } ;;
      *) log "the tree is dirty on $br; leaving it"; return ;;
    esac
  fi
  cgit 60 switch -q main && cgit 60 merge -q --ff-only "$SNAP" \
    || pause environment "could not bring main up to date with $SNAP" "$slice"
}

# --- Lock, main's scripts, main loop -----------------------------------------------------------------------------
mkdir -p "$logs/failures" "$logs/mentioned" || { echo "run.sh: cannot create $logs" >&2; exit 1; }
# run.lock is a directory (mkdir is atomic) holding the PID. A live PID whose command is a run.sh is another runner;
# this PID is this runner restarted by exec; anything else is stale.
if ! mkdir "$lock" 2> /dev/null; then
  holder="$(cat "$lock/pid" 2> /dev/null)"
  if [ "$holder" != "$$" ]; then
    if number "$holder" && holder_cmd="$(ps -p "$holder" -o command= 2> /dev/null)" && [[ "$holder_cmd" == *run.sh* ]]; then
      echo "run.sh: already running (pid $holder holds $lock)" >&2; exit 2
    fi
    rm -rf "$lock"
    mkdir "$lock" 2> /dev/null || { echo "run.sh: could not take $lock" >&2; exit 2; }
    log "took over a stale lock (pid ${holder:-none})"
  fi
fi
echo $$ > "$lock/pid"
guard_dir="$(mktemp -d)"
iter_dir=""
scripts=""
drop_scripts() { [ -n "$iter_dir" ] && rm -rf "$iter_dir"; iter_dir=""; return 0; }
cleanup() {
  [ "$(cat "$lock/pid" 2> /dev/null)" = "$$" ] && rm -rf "$lock"
  rm -rf "$guard_dir"
  drop_scripts
}
on_signal() {
  [ -n "$child_pid" ] && kill -TERM -- "-$child_pid" 2> /dev/null
  [ -n "$watcher_pid" ] && kill -TERM -- "-$watcher_pid" 2> /dev/null
  log "stopped by a signal"
  exit 130
}
trap cleanup EXIT
trap on_signal INT TERM HUP
cd "$repo" || { log "cannot enter $repo"; exit 1; }
log "runner started (pid $$, from $0)"

failed_copy_sha=""
# load_scripts: fetches main, restarts on a new run.sh, and points $scripts at main's scripts/agent (or the
# override); pauses and fails when any of it fails.
load_scripts() {
  cgit 300 fetch -q "$remote" "+refs/heads/main:$SNAP" || { pause transient "fetching main from $remote failed" ""; return 1; }
  snap_sha="$(g rev-parse "$SNAP" 2> /dev/null)"
  if [ "$snap_sha" != "$failed_copy_sha" ]; then
    if g show "$SNAP:scripts/agent/run.sh" > "$logs/.run.sh.new" 2>> "$logs/runner.log"; then
      if ! cmp -s "$logs/.run.sh.new" "$0"; then
        if mv -f "$logs/.run.sh.new" "$home/runner/run.sh" 2>> "$logs/runner.log"; then
          log "main has a new run.sh; restarting from it"
          rm -rf "$guard_dir"; drop_scripts
          AGENT_RUNNER_COPY=1 AGENT_REPO="$repo" exec bash "$home/runner/run.sh"
        fi
        # Not retried until main moves again, so a failing move cannot loop.
        failed_copy_sha="$snap_sha"
        log "could not install main's new run.sh ($snap_sha); keeping the running copy"
      fi
    else
      failed_copy_sha="$snap_sha"; log "main has no readable scripts/agent/run.sh ($snap_sha); keeping the running copy"
    fi
    rm -f "$logs/.run.sh.new"
  fi
  if [ -n "$scripts_override" ]; then scripts="$scripts_override"; return 0; fi
  iter_dir="$(mktemp -d)" || { pause transient "cannot make a temp dir" ""; return 1; }
  if ! g archive "$SNAP" scripts/agent 2>> "$logs/runner.log" | tar -x -C "$iter_dir" 2>> "$logs/runner.log"; then
    pause transient "cannot extract scripts/agent from $SNAP" ""; return 1
  fi
  scripts="$iter_dir/scripts/agent"
}

while :; do
  drop_scripts
  stopped && break
  paused=0
  cur_item="-"
  load_scripts || continue
  make_true || continue
  stopped && break
  call 300 bash "$scripts/pending.sh"; prc=$?
  line="$call_out"
  stopped && break
  if [ "$prc" != 0 ]; then pause transient "pending.sh exited $prc" ""; continue; fi
  if [ -z "$line" ]; then end_episode; idle; continue; fi
  rm -f "$logs/idle"

  kind="" a="" b="" extra=""
  read -r kind a b extra <<<"$line"
  pr=- slice=""
  case "$kind" in
    new|resume) number "$a" && [ -z "$b" ] && slice="$a" ;;
    fix|cap|review|merge|owner) number "$a" && number "$b" && [ -z "$extra" ] && { pr="$a"; slice="$b"; } ;;
  esac
  if [ -z "$slice" ]; then pause transient "unreadable pending.sh line: $line" ""; continue; fi
  last_slice="$slice"
  cur_item="$kind-$pr-$slice"
  log "next: $line"

  cur_sha=- cur_labels=""
  if [ "$pr" != - ]; then
    # After a merge the runner is done with a pull request: an item for one that is merged or closed is dropped.
    if was_merged "$pr"; then gone "$pr" merged "$line"; continue; fi
    head_of "$pr" || { pause transient "could not read the head of #$pr" "$slice"; continue; }
    case "$head_state" in
      MERGED) note_merged "$pr"; gone "$pr" merged "$line"; continue ;;
      CLOSED) gone "$pr" closed "$line"; continue ;;
    esac
    cur_sha="$head_sha" cur_labels="$head_labels"
  fi
  # R55: a Block left half done is finished before anything else runs for this slice.
  if [ "$(failures "$slice")" -ge 2 ]; then
    item="$kind $slice"; [ "$pr" = - ] || item="$kind $pr $slice"
    block_slice "$slice" "$pr" "$cur_sha" "$item" "the earlier Blocked did not go through" ""
    continue
  fi
  if [ "$pr" != - ]; then pr_gate "$kind" "$pr" "$slice" || continue; fi
  case "$kind" in
    new|resume|fix|review) claude_item "$kind" "$pr" "$slice" ;;
    merge|owner) merge_item "$pr" "$slice" ;;
    cap) cap_item "$pr" "$slice" ;;
  esac
  stopped || back_to_main "$kind" "$slice"
  [ "$paused" = 0 ] && end_episode
done
log "the stop file exists; stopping"
exit 0
