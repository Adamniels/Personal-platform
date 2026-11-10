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
# Work items (one line from pending.sh) and what the runner does with each:
#   new <slice>, resume <slice>, fix <pr> <slice>
#                        claude -p "/pick-up-slice <slice>" --permission-mode auto --disallowedTools <patterns>,
#                        with a copy of gh-guard.sh first on PATH as `gh` (R25, R36). Before the run a dirty tree on
#                        the slice's branch is committed locally as "Checkpoint: interrupted run" (resume and fix;
#                        never pushed), and a dirty tree anywhere else is stashed, mentioned, and paused on (R16).
#   review <pr> <slice>  claude -p "Use the reviewer agent on pull request <pr>" --permission-mode auto. A review
#                        that leaves no verdict for the PR's head is a failed run (R32).
#   merge <pr> <slice>   merge-gate.sh <pr>. Exit 3 with `ci-ok is pending` among the reasons: wait the poll
#                        interval (R45), with one mention once the same head has waited 2 h (R50). Otherwise, only
#                        `risk is not low` and `touches protected paths`: "ready for you" and needs-owner; any other
#                        reasons: a mention with them and needs-owner. Exit 1 or another code: a transient pause.
#   cap <pr> <slice>     a mention that the review rounds ran out, and needs-owner.
#   owner <pr> <slice>   a needs-owner PR approved for its head: a mention that it is ready to read and merge.
#   (nothing)            idle: one mention per empty queue, on the most recently merged slice (without one, a log
#                        line), then the poll interval.
# Mentions go on the slice issue. Every mention about a PR (ready, refused, cap, owner, ci-ok waiting) is made once per
# PR and head sha: agent-logs/owner-told holds "<pr> <sha>" lines (R42), and an item whose mention was already made
# waits the poll interval instead of looping (R43b). After every item a dirty tree on the slice's own branch left by
# its implementer run is checkpointed, dirt anywhere else after a claude run is stashed with a mention, and a clean
# tree is switched to main and fast-forwarded to refs/runner/main (a failure is an environment pause).
#
# Every claude run is classified (R13, R35) as ok, limit, transient, environment or task, and only task counts
# against the slice. A last line starting `ENVIRONMENT:` is environment; `Stopped:` or `Blocked:` is task; any other
# exit 0 is ok. A failed run is limit when its last 20 lines carry a usage limit message (`usage limit reached`,
# `limit reached|<epoch>`, `hit your limit`, `rate_limit_error`, `HTTP 429`, `status 429`), transient on a network or
# API error, and otherwise task when it lasted at least 120 s, transient when faster. The same item at the same PR
# head coming back after an ok run is task too (no progress; agent-logs/last-ok). Two task failures in a row set the
# slice Blocked, add needs-owner to its PR and record "<pr> <sha>" in agent-logs/blocked-prs, with one mention; a PR
# item at a recorded pr and sha afterwards waits the poll interval and runs nothing, until the head moves (R47). An ok
# run resets the count.
# limit, transient and environment pause and then ask pending.sh again, changing nothing on the board (R14, R46):
# limit sleeps until the reset in the output plus 60 s (an epoch after `|`, or a `resets 3pm` style time), else
# 15 min doubling to 60 min, and mentions only once its episode passes 6 h or 10 limits in a row on one item;
# transient and environment sleep 1 min doubling to 30 min and mention once per episode past 30 min. Before every
# claude run the runner probes `gh api rate_limit`, `docker info` and free disk over 5 GB; a failed probe is an
# environment pause with no claude call (R15). pending.sh failing is a transient pause. Every external call runs
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
# claude, gh, docker, git, df, date and jq come from PATH.
# Exit codes: 0 stopped by the stop file, 1 cannot fetch main, copy the runner, or set up agent-logs or the
# repository, 2 already running (run.lock holds the PID of a live run.sh) or a bad environment value, 130 a signal.
set -uo pipefail

OWNER="Adamniels"
REPO_SLUG="Adamniels/Personal-platform"
BOT="mac-mini-agent"
LABEL="needs-owner"
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
logs="$home/agent-logs"
stop_file="$home/.agent-stop"
lock="$logs/run.lock"             # a directory holding pid
pause_file="$logs/pause"
owner_told="$logs/owner-told"
blocked_prs="$logs/blocked-prs"
ci_pending="$logs/ci-pending"
last_ok_file="$logs/last-ok"

[[ "$poll" =~ ^[0-9]+$ ]] || { echo "run.sh: AGENT_POLL_SECONDS must be a number: $poll" >&2; exit 2; }
[[ "$timeout_secs" =~ ^[1-9][0-9]*$ ]] || { echo "run.sh: AGENT_TIMEOUT_SECONDS must be a positive number: $timeout_secs" >&2; exit 2; }
[ -z "$call_override" ] || [[ "$call_override" =~ ^[1-9][0-9]*$ ]] \
  || { echo "run.sh: AGENT_CALL_TIMEOUT_SECONDS must be a positive number: $call_override" >&2; exit 2; }

# Git without the repository's hooks (R49): a branch can carry a hook, and the runner must not run it.
g() { git -c core.hooksPath=/dev/null -C "$repo" "$@"; }

# Start from main's run.sh, copied out of the working tree. The copy is moved into place, so a runner already
# reading an older copy keeps its own file. AGENT_RUNNER_COPY marks the copy and is not passed on to children.
if [ -z "${AGENT_RUNNER_COPY:-}" ]; then
  mkdir -p "$home/runner" || { echo "run.sh: cannot create $home/runner" >&2; exit 1; }
  g fetch -q "$remote" "+refs/heads/main:$SNAP" || { echo "run.sh: cannot fetch main from $remote; not starting" >&2; exit 1; }
  g show "$SNAP:scripts/agent/run.sh" > "$home/runner/run.sh.$$" && mv -f "$home/runner/run.sh.$$" "$home/runner/run.sh" \
    || { rm -f "$home/runner/run.sh.$$"; echo "run.sh: main has no readable scripts/agent/run.sh; not starting" >&2; exit 1; }
  AGENT_RUNNER_COPY=1 AGENT_REPO="$repo" exec bash "$home/runner/run.sh" "$@"
fi
unset AGENT_RUNNER_COPY

# Implementer runs refuse these Bash commands (R25, I5); settings.json and the gh guard cover the same ground.
DENY=("Bash(*verdict:*)" "Bash(*verdict: *)" "Bash(*<!--*)" "Bash(*merge-gate.sh*)"
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
call() {
  local t="$1" name rc
  shift
  [ -n "$call_override" ] && t="$call_override"
  name="$1"; [ "$1" = bash ] || [ "$1" = git ] && name="$1 ${2:-}"
  [ "$1" = git ] && name="git ${4:-}"   # git -c <x> -C <repo> <verb>
  with_timeout "$t" "$logs/.call.out" "$logs/.call.err" "$@"; rc=$?
  call_out="$(cat "$logs/.call.out" 2> /dev/null)"
  log_lines "$(basename "$name")" "$logs/.call.err"
  [ "$rc" = 124 ] && log "$name timed out after $t s"
  return "$rc"
}
cgit() { local t="$1"; shift; call "$t" git -c core.hooksPath=/dev/null -C "$repo" "$@"; }

# --- GitHub --------------------------------------------------------------------------------------------------------
comment() { call 120 gh issue comment "$1" --repo "$REPO_SLUG" --body "$2"; }
add_label() { call 120 gh pr edit "$1" --repo "$REPO_SLUG" --add-label "$LABEL"; }
# head_of <pr>: sets $head_sha, or fails.
head_sha=""
head_of() {
  head_sha=""
  call 120 gh pr view "$1" --repo "$REPO_SLUG" --json headRefOid --jq .headRefOid || return 1
  [[ "$call_out" =~ ^[0-9a-f]+$ ]] || { log "unreadable head of #$1: $call_out"; return 1; }
  head_sha="$call_out"
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
# hand_over <pr> <sha> <slice> <body>: tells the owner once for this head, then adds needs-owner; when the mention was
# made before, waits the poll interval, so a repeated item cannot spin (R43b).
hand_over() {
  tell_owner "$1" "$2" "$3" "$4" && add_label "$1" || return 1
  [ "$already_told" = 1 ] && nap "$poll"
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
  add_label "$2" || return 1
  blocked_pr "$2" "$3" || echo "$2 $3" >> "$blocked_prs"
}
block_slice() { # slice pr sha item short logfile
  local n see=""
  n="$(failures "$1")"
  [ -n "$6" ] && see=" See $6 on the Mac mini."
  if escalate "blocked-$1" "$1" "@$OWNER slice #$1 is Blocked: $n runs in a row failed on it, the last one $4 ($5).$see" do_block "$1" "$2" "$3"; then
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

probe_reason=""
probe() {
  local free
  call 120 gh api rate_limit || { probe_reason="gh api rate_limit failed"; return 1; }
  call 60 docker info || { probe_reason="docker info failed"; return 1; }
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
  local kind="$1" pr="$2" slice="$3" sha="$cur_sha" item logf n start rc secs cls short last key vk vs
  item="$kind $slice"; [ "$pr" = - ] || item="$kind $pr $slice"
  if [ "$(failures "$slice")" -ge 2 ]; then block_slice "$slice" "$pr" "$sha" "$item" "the earlier Blocked did not go through" ""; return; fi
  probe || { pause environment "$probe_reason" "$slice"; return; }
  if [ "$kind" != review ]; then
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
    with_timeout "$timeout_secs" "$logf" "" env PATH="$guard_dir:$PATH" \
      claude -p "/pick-up-slice $slice" --permission-mode auto --disallowedTools "${DENY[@]}"
  fi
  rc=$?
  secs=$(( $(now) - start ))
  cls="$(classify_run "$rc" "$secs" "$logf")"
  last="$(grep -v '^[[:space:]]*$' "$logf" 2> /dev/null | tail -n1)"
  short="exit $rc after $secs s"; [ "$rc" = 124 ] && short="timed out after $secs s"

  if [ "$kind" = review ] && [ "$cls" = ok ]; then
    call 120 bash "$scripts/verdict.sh" "$pr" || { pause transient "verdict.sh failed after the review of #$pr" "$slice"; return; }
    read -r vk vs _ <<<"$call_out"
    head_of "$pr" || { pause transient "could not read the head of #$pr" "$slice"; return; }
    if [ "$vk" = none ] || [ "$vs" != "$head_sha" ]; then
      log "the review of #$pr left no verdict for head $head_sha"
      cls="$(classify_run 1 "$secs" "$logf")"
      short="the review left no verdict for head $head_sha"
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
}

# --- Merge, cap, owner, idle -------------------------------------------------------------------------------------
# ci_wait <pr> <sha> <slice>: notes when ci-ok was first seen pending on this head; once 2 h ago, one mention (R50).
ci_wait() {
  local first t
  t="$(now)"
  first="$(awk -v k="$1 $2" '$1 " " $2 == k { print $3; exit }' "$ci_pending" 2> /dev/null)"
  if ! number "$first"; then
    { grep -v "^$1 " "$ci_pending" 2> /dev/null; echo "$1 $2 $t"; } > "$ci_pending.new" && mv -f "$ci_pending.new" "$ci_pending"
    first="$t"
  fi
  if [ $((t - first)) -ge "$CI_MENTION_AFTER" ] && ! told "$1" "$2"; then
    tell_owner "$1" "$2" "$3" "@$OWNER ci-ok has been pending on PR #$1 for $(( (t - first) / 60 )) min at head $2. The runner keeps waiting for it." \
      || log "could not mention the ci-ok wait on #$3"
  fi
}
ci_clear() { [ -f "$ci_pending" ] && { grep -v "^$1 " "$ci_pending" > "$ci_pending.new"; mv -f "$ci_pending.new" "$ci_pending"; }; return 0; }

merge_item() { # pr slice; head $cur_sha
  local pr="$1" slice="$2" out rc reasons joined r owner_only=1
  call 300 bash "$scripts/merge-gate.sh" "$pr"; rc=$?
  out="$call_out"
  case "$rc" in
    0) log "merged #$pr: $out"; reset_failures "$slice"; ci_clear "$pr" ;;
    3)
      reasons="$(grep -v '^[[:space:]]*$' <<<"$out")"
      if grep -qxF "ci-ok is pending" <<<"$reasons"; then
        log "#$pr waits for ci-ok ($(tr '\n' ',' <<<"$reasons"))"; ci_wait "$pr" "$cur_sha" "$slice"; nap "$poll"; return
      fi
      ci_clear "$pr"
      [ -n "$reasons" ] || owner_only=0
      while IFS= read -r r; do
        case "$r" in "risk is not low"|"touches protected paths") ;; *) owner_only=0 ;; esac
      done <<<"$reasons"
      joined="$(awk 'NR > 1 { printf ", " } { printf "%s", $0 }' <<<"${reasons:-no reasons printed}")"
      log "the merge gate refused #$pr: $joined"
      if [ "$owner_only" = 1 ]; then
        hand_over "$pr" "$cur_sha" "$slice" "@$OWNER PR #$pr is ready for you: the merge gate leaves it to the owner ($joined)." \
          || pause transient "could not hand #$pr to the owner" "$slice"
      else
        hand_over "$pr" "$cur_sha" "$slice" "@$OWNER PR #$pr needs you: the merge gate refused it ($joined)." \
          || pause transient "could not hand #$pr to the owner" "$slice"
      fi ;;
    *) pause transient "merge-gate.sh exited $rc for #$pr" "$slice" ;;
  esac
}

cap_item() { # pr slice
  hand_over "$1" "$cur_sha" "$2" "@$OWNER PR #$1 has used up its review rounds: the reviewer asked for changes a fourth time, so it waits for you." \
    || pause transient "could not hand #$1 to the owner" "$2"
}

owner_item() { # pr slice
  if told "$1" "$cur_sha"; then log "#$1 still waits for the owner at $cur_sha"; nap "$poll"; return; fi
  tell_owner "$1" "$cur_sha" "$2" "@$OWNER the reviewed PR #$1 is ready for you to read and merge (head $cur_sha)." \
    || pause transient "could not tell the owner about #$1" "$2"
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

# load_scripts: fetches main, restarts on a new run.sh, and points $scripts at main's scripts/agent (or the
# override); pauses and fails when any of it fails.
load_scripts() {
  cgit 300 fetch -q "$remote" "+refs/heads/main:$SNAP" || { pause transient "fetching main from $remote failed" ""; return 1; }
  if g show "$SNAP:scripts/agent/run.sh" > "$home/runner/run.sh.new" 2>> "$logs/runner.log"; then
    if ! cmp -s "$home/runner/run.sh.new" "$0"; then
      mv -f "$home/runner/run.sh.new" "$home/runner/run.sh"
      log "main has a new run.sh; restarting from it"
      rm -rf "$guard_dir"; drop_scripts
      AGENT_RUNNER_COPY=1 AGENT_REPO="$repo" exec bash "$home/runner/run.sh"
    fi
    rm -f "$home/runner/run.sh.new"
  else
    rm -f "$home/runner/run.sh.new"; log "main has no scripts/agent/run.sh; keeping the running copy"
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

  cur_sha=-
  if [ "$pr" != - ]; then
    head_of "$pr" || { pause transient "could not read the head of #$pr" "$slice"; continue; }
    cur_sha="$head_sha"
    if blocked_pr "$pr" "$cur_sha"; then
      log "#$pr is Blocked at $cur_sha; waiting for a new head"; end_episode; nap "$poll"; continue
    fi
  fi
  case "$kind" in
    new|resume|fix|review) claude_item "$kind" "$pr" "$slice" ;;
    merge) merge_item "$pr" "$slice" ;;
    cap) cap_item "$pr" "$slice" ;;
    owner) owner_item "$pr" "$slice" ;;
  esac
  stopped || back_to_main "$kind" "$slice"
  [ "$paused" = 0 ] && end_episode
done
log "the stop file exists; stopping"
exit 0
