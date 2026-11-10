#!/usr/bin/env bash
# The unattended runner on the Mac mini: asks pending.sh for the next work item, does it, and loops until the stop
# file exists. Started by the runbook as `caffeinate -is bash scripts/agent/run.sh` in tmux.
#
#   bash scripts/agent/run.sh
#
# Work items (one line from pending.sh) and what the runner does with each:
#   new <slice>, resume <slice>, fix <pr> <slice>
#                        claude -p "/pick-up-slice <slice>" --permission-mode auto --disallowedTools <patterns>,
#                        with a copy of gh-guard.sh first on PATH as `gh` (R25, R36). Before resume and fix, a dirty
#                        tree on the slice's branch is committed locally as "Checkpoint: interrupted run" (never
#                        pushed); a dirty tree on any other branch is stashed, mentioned, and paused on (R16).
#   review <pr> <slice>  claude -p "Use the reviewer agent on pull request <pr>" --permission-mode auto. A review
#                        that leaves no verdict for the PR's head is a failed run (R32).
#   merge <pr> <slice>   merge-gate.sh <pr>. Exit 3 with only `ci-ok is pending`: wait the poll interval. Only
#                        `risk is not low` and `touches protected paths`: needs-owner and "ready for you". Any other
#                        reasons: needs-owner and a mention with them. Exit 1 or another code: a transient pause.
#   cap <pr> <slice>     needs-owner on the PR and a mention that its review rounds ran out.
#   owner <pr> <slice>   a needs-owner PR approved for its head: one mention that it is ready for the owner to read
#                        and merge. The "ready for you", cap and owner mentions are made once per PR and head sha
#                        (agent-logs/owner-told holds "<pr> <sha>" lines), so the owner hears of a head once (R42);
#                        an owner item already told waits the poll interval, like idle.
#   (nothing)            idle: one mention per empty queue, on the most recently merged slice, then the poll
#                        interval. Without a merged slice the runner only logs.
# Mentions go on the slice issue. A mention and the action after it (a label, Blocked) are kept apart, by
# owner-told or a marker in agent-logs/mentioned, so a retried action never mentions twice.
#
# Every claude run is classified (R13, R35) as ok, limit, transient, environment or task, and only task counts
# against the slice: an `ENVIRONMENT:` last line is environment; a `Stopped:` or `Blocked:` last line is task;
# usage or rate limit text is limit and network or API trouble is transient (the last 20 lines of a failed run,
# the last line of an ok one); any other nonzero exit or timeout is task when the run lasted at least 120 s,
# else transient. The same item coming back after an ok run is task too (no progress). Two task failures in a row
# set the slice Blocked (and needs-owner on its PR), with one mention; an ok run resets the count.
# limit, transient and environment pause and then ask pending.sh again, changing nothing on the board (R14):
# limit sleeps until the reset time in the output plus 60 s (an epoch after `|`, or `resets 3pm` style), else
# 15 min doubling to 60 min, and never mentions; transient and environment sleep 1 min doubling to 30 min and
# mention once per episode, after the episode passes 30 min. Before every claude run the runner probes
# `gh api rate_limit`, `docker info` and free disk over 5 GB; a failed probe is an environment pause with no
# claude call (R15). pending.sh failing is a transient pause.
# Sleeps go through AGENT_SLEEP in chunks of at most 30 s, checking the stop file between them.
#
# What runs is main's, never a slice branch's (R43). At start the runner copies itself to $AGENT_HOME/runner/run.sh
# and execs the copy, so a branch switch cannot change the running script. Every loop it runs
# `git fetch origin main` (a failure is a transient pause) and extracts scripts/agent from origin/main into a fresh
# temp dir, and calls pending.sh, verdict.sh, merge-gate.sh, board.sh and gh-guard.sh from there. After every work
# item a clean tree is switched to main and fast-forwarded to origin/main (a failure is an environment pause), so the
# next claude session loads main's skills, reviewer and settings; a dirty tree is left for R16's checkpoint.
#
# Environment (defaults in brackets):
#   AGENT_HOME             [$HOME] holds the stop file .agent-stop and agent-logs/: runner.log, one
#                          <slice>-<YYYYmmdd-HHMMSS>.log per claude run, failures/<slice>, pause, idle, run.lock
#   AGENT_SCRIPTS          [scripts/agent extracted from origin/main each loop] a fixed directory holding pending.sh,
#                          merge-gate.sh, board.sh, verdict.sh and gh-guard.sh instead: a test override
#   AGENT_REPO             [the repository holding this script] the clone the agents work in
#   AGENT_POLL_SECONDS     [600] idle and ci-ok pending wait
#   AGENT_TIMEOUT_SECONDS  [5400] a claude run is killed, with its process group, after this long
#   AGENT_SLEEP            [sleep] the sleep command
# claude, gh, docker, git, df, date and jq come from PATH.
# Exit codes: 0 stopped by the stop file, 1 cannot set up agent-logs or the repository, 2 already running
# (run.lock holds a live PID) or a bad environment value.
set -uo pipefail

OWNER="Adamniels"
REPO_SLUG="Adamniels/Personal-platform"
BOT="mac-mini-agent"
LABEL="needs-owner"
MIN_TASK_SECONDS=120          # a failure faster than this is not the slice's fault (R13)
MIN_FREE_KB=$((5 * 1024 * 1024))
CHUNK=30                      # longest single sleep, so the stop file is seen quickly
MENTION_AFTER=1800            # a transient or environment episode mentions the owner once past this
LIMIT_RE='usage limit|rate limit|limit reached|hit your limit|resets|(^|[^0-9])429([^0-9]|$)'
TRANSIENT_RE='connection error|econnreset|econnrefused|etimedout|enotfound|eai_again|socket hang up|fetch failed|network error|api error: ?5[0-9][0-9]|overloaded|(^|[^0-9])529([^0-9]|$)'

here="$(cd "$(dirname "$0")" && pwd)"
scripts_override="${AGENT_SCRIPTS:-}"
scripts=""      # set every loop: the override, or scripts/agent extracted from origin/main
home="${AGENT_HOME:-$HOME}"
repo="${AGENT_REPO:-$(cd "$here/../.." && pwd)}"
poll="${AGENT_POLL_SECONDS:-600}"
timeout_secs="${AGENT_TIMEOUT_SECONDS:-5400}"
sleep_cmd="${AGENT_SLEEP:-sleep}"
logs="$home/agent-logs"
stop_file="$home/.agent-stop"
lock="$logs/run.lock"
pause_file="$logs/pause"
owner_told="$logs/owner-told"

[[ "$poll" =~ ^[0-9]+$ ]] || { echo "run.sh: AGENT_POLL_SECONDS must be a number: $poll" >&2; exit 2; }
[[ "$timeout_secs" =~ ^[1-9][0-9]*$ ]] || { echo "run.sh: AGENT_TIMEOUT_SECONDS must be a positive number: $timeout_secs" >&2; exit 2; }

# Run a copy outside the working tree, so switching branches never edits the running script. The copy is moved
# into place, so a runner already reading an older copy keeps its own file.
if [ -z "${AGENT_RUNNER_COPY:-}" ]; then
  mkdir -p "$home/runner" && cp "$0" "$home/runner/run.sh.$$" && mv -f "$home/runner/run.sh.$$" "$home/runner/run.sh" \
    || { echo "run.sh: cannot copy the runner to $home/runner" >&2; exit 1; }
  AGENT_RUNNER_COPY=1 AGENT_REPO="$repo" exec bash "$home/runner/run.sh" "$@"
fi

# Implementer runs refuse these Bash commands (R25); settings.json and the gh guard cover the same ground.
DENY=("Bash(*verdict:*)" "Bash(*merge-gate.sh*)" "Bash(*gh pr merge*)" "Bash(*pulls/*/merge*)"
      "Bash(*--edit-last*)" "Bash(*--delete-last*)")
for m in PATCH DELETE patch delete; do
  DENY+=("Bash(*gh api *-X $m*)" "Bash(*gh api *-X$m*)" "Bash(*gh api *--method $m*)" "Bash(*gh api *--method=$m*)")
done

# --- Basics ------------------------------------------------------------------------------------------------------
now() { date +%s; }
log() { printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" | tee -a "$logs/runner.log"; }
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
# Agent-written text in a mention: no mentions, no code spans, one line, short.
sanitize() { printf '%s' "$1" | tr -d '`@\r' | tr '\n' ' ' | cut -c1-200; }
number() { [[ "${1:-}" =~ ^[0-9]+$ ]]; }

# with_timeout <seconds> <log> <command...>: runs the command in its own process group with stdin from /dev/null and
# output to <log>, and kills the whole group (TERM, then KILL after 10 s) when it outlives <seconds>. Returns the
# command's exit code, or 124 on a timeout.
child_pid=""
with_timeout() {
  local t="$1" out="$2" fired="$logs/.timeout.$$" pid watcher rc
  shift 2
  rm -f "$fired"
  set -m
  "$@" < /dev/null > "$out" 2>&1 &
  pid=$!
  ( sleep "$t"; : > "$fired"; kill -TERM -- "-$pid" 2> /dev/null; sleep 10; kill -KILL -- "-$pid" 2> /dev/null ) \
    < /dev/null > /dev/null 2>&1 &
  watcher=$!
  set +m
  child_pid="$pid"
  wait "$pid"; rc=$?
  child_pid=""
  kill -TERM -- "-$watcher" 2> /dev/null; wait "$watcher" 2> /dev/null
  kill -TERM -- "-$pid" 2> /dev/null   # whatever the command left behind in its group
  if [ -e "$fired" ]; then rm -f "$fired"; return 124; fi
  return "$rc"
}

# --- GitHub writes -----------------------------------------------------------------------------------------------
comment() { gh issue comment "$1" --repo "$REPO_SLUG" --body "$2" > /dev/null 2>> "$logs/runner.log"; }
add_label() { gh pr edit "$1" --repo "$REPO_SLUG" --add-label "$LABEL" > /dev/null 2>> "$logs/runner.log"; }
# escalate <key> <issue> <body> <action...>: mentions once, then runs the action; the marker keeps a retried action
# from mentioning again. Returns 1 when the mention or the action failed.
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
head_of() { gh pr view "$1" --repo "$REPO_SLUG" --json headRefOid --jq .headRefOid 2>> "$logs/runner.log"; }
told() { grep -qxF "$1 $2" "$owner_told" 2> /dev/null; }
# tell_owner <pr> <sha> <slice> <body>: mentions once per PR and head sha (R42).
already_told=0
tell_owner() {
  already_told=0
  if told "$1" "$2"; then log "the owner was already told about #$1 at $2"; already_told=1; return 0; fi
  comment "$3" "$4" || { log "could not comment on #$3"; return 1; }
  echo "$1 $2" >> "$owner_told"
}
# hand_over <pr> <slice> <body>: tells the owner once for the PR's head, then adds needs-owner. A hand-over whose
# mention was already made waits the poll interval, so a repeated item cannot spin (R43b).
hand_over() {
  local sha
  sha="$(head_of "$1")" && [ -n "$sha" ] && tell_owner "$1" "$sha" "$2" "$3" && add_label "$1" || return 1
  [ "$already_told" = 1 ] && nap "$poll"
  return 0
}
# The slice closed by the most recently merged bot PR, or nothing.
merged_slice() {
  local out
  out="$(gh pr list --repo "$REPO_SLUG" --state merged --author "$BOT" --limit 30 \
    --json number,mergedAt,headRefName,closingIssuesReferences 2>> "$logs/runner.log")" || return 1
  jq -r 'sort_by(.mergedAt) | last | if . == null then empty else
    ((.closingIssuesReferences[0].number // empty | tostring), ((.headRefName // "") | capture("^(?<n>[0-9]+)-").n)?)
    end' <<<"$out" 2> /dev/null | head -n1
}

# --- Failures and pauses -----------------------------------------------------------------------------------------
failures() { local n; n="$(cat "$logs/failures/$1" 2> /dev/null)"; number "$n" && echo "$n" || echo 0; }
reset_failures() { rm -f "$logs/failures/$1"; }

do_block() { # slice pr
  bash "$scripts/board.sh" status "$1" Blocked >> "$logs/runner.log" 2>&1 || return 1
  [ "$2" = - ] || add_label "$2"
}
block_slice() { # slice pr item why
  local n; n="$(failures "$1")"
  if escalate "blocked-$1" "$1" "@$OWNER slice #$1 is Blocked: $n runs in a row failed on it, the last one $3 ($(sanitize "$4")). The logs are in $logs on the Mac mini." do_block "$1" "$2"; then
    log "slice $1 set Blocked after $n failures"
    reset_failures "$1"
  else
    pause transient "could not set slice $1 Blocked" "$1"
  fi
}
count_failure() { # slice pr item why
  local n=$(( $(failures "$1") + 1 ))
  echo "$n" > "$logs/failures/$1"
  log "task failure $n for slice $1 ($3): $4"
  [ "$n" -ge 2 ] && block_slice "$@"
  return 0
}

# reset_wait <log>: seconds until the usage limit's reset plus 60 s, from an epoch after `|` or a `resets 3pm`,
# `resets at 15:00` style time (the next one, local time). Fails when the log names no usable reset.
reset_wait() {
  local text epoch target t H M S h m ap re line midnight w
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
  echo "$w"
}

# pause <limit|transient|environment> <reason> <slice or empty> [<claude log>]: no count, no board change; sleeps by
# the episode's backoff, mentioning once per episode for transient and environment trouble past 30 min.
paused=0
pause() {
  local kind="$1" reason="$2" slice="$3" runlog="${4:-}" t start ln on men w target
  paused=1
  t="$(now)"
  read -r start ln on men 2> /dev/null < "$pause_file"
  number "${start:-}" && number "${ln:-}" && number "${on:-}" && number "${men:-}" || { start="$t" ln=0 on=0 men=0; }
  if [ "$kind" = limit ]; then
    w=""; [ -n "$runlog" ] && w="$(reset_wait "$runlog")"
    if [ -z "$w" ]; then w=$((900 << ln)); [ "$w" -gt 3600 ] && w=3600; [ "$ln" -lt 3 ] && ln=$((ln + 1)); fi
  else
    w=$((60 << on)); [ "$w" -gt 1800 ] && w=1800; [ "$on" -lt 5 ] && on=$((on + 1))
    if [ "$men" = 0 ] && [ $((t - start)) -gt "$MENTION_AFTER" ]; then
      target="${slice:-$last_slice}"; [ -n "$target" ] || target="$(merged_slice)"
      if [ -z "$target" ]; then
        log "paused for $(( (t - start) / 60 )) min, and no slice to mention the owner on"; men=1
      elif comment "$target" "@$OWNER the runner has been paused for $(( (t - start) / 60 )) min: $kind, $(sanitize "$reason"). It retries on its own and has changed nothing on the board."; then
        log "mentioned the owner on #$target about the pause"; men=1
      fi
    fi
  fi
  echo "$start $ln $on $men" > "$pause_file"
  log "pausing ($kind: $reason) for $w s"
  nap "$w"
}
end_episode() { [ -e "$pause_file" ] && { log "the pause episode is over"; rm -f "$pause_file"; }; return 0; }

# --- Claude runs -------------------------------------------------------------------------------------------------
# classify_run <exit> <seconds> <log>: prints ok, limit, transient, environment or task (R13, R35).
classify_run() {
  local code="$1" secs="$2" text last scope
  text="$(grep -v '^[[:space:]]*$' "$3" 2> /dev/null | tail -n 20)"
  last="$(tail -n1 <<<"$text")"
  case "$last" in
    ENVIRONMENT:*) echo environment; return ;;
    Stopped:*|Blocked:*) echo task; return ;;
  esac
  scope="$text"; [ "$code" = 0 ] && scope="$last"
  if grep -Eiq "$LIMIT_RE" <<<"$scope"; then echo limit; return; fi
  if grep -Eiq "$TRANSIENT_RE" <<<"$scope"; then echo transient; return; fi
  if [ "$code" = 0 ]; then echo ok
  elif [ "$secs" -ge "$MIN_TASK_SECONDS" ]; then echo task
  else echo transient
  fi
}

probe() {
  local free
  gh api rate_limit > /dev/null 2>&1 || { probe_reason="gh api rate_limit failed"; return 1; }
  docker info > /dev/null 2>&1 || { probe_reason="docker info failed"; return 1; }
  free="$(df -Pk "$repo" 2> /dev/null | awk 'NR == 2 { print $4 }')"
  if ! number "$free" || [ "$free" -le "$MIN_FREE_KB" ]; then probe_reason="free disk is ${free:-unknown} KB, under 5 GB"; return 1; fi
  return 0
}

# prepare_tree <kind> <slice>: 0 when the tree is clean or checkpointed; otherwise it pauses and returns 1.
prepare_tree() {
  local kind="$1" slice="$2" st br ts
  st="$(git -C "$repo" status --porcelain 2>&1)" || { pause environment "git status failed: $st" "$slice"; return 1; }
  [ -z "$st" ] && return 0
  br="$(git -C "$repo" branch --show-current 2> /dev/null)"
  ts="$(date '+%Y-%m-%d %H:%M:%S')"
  if [ "$kind" != new ] && [ -n "$br" ] && [[ "$br" == "$slice-"* ]]; then
    if git -C "$repo" add -A >> "$logs/runner.log" 2>&1 \
       && git -C "$repo" commit -q -m "Checkpoint: interrupted run" \
            -m "The runner found uncommitted changes on $br at $ts, before a $kind run, left by an interrupted run. They are committed as they were, unverified and not pushed; pick-up-slice inspects, finishes and gates them." \
            >> "$logs/runner.log" 2>&1; then
      log "committed a checkpoint of the interrupted work on $br"
      return 0
    fi
    pause environment "could not commit the checkpoint on $br" "$slice"; return 1
  fi
  if ! git -C "$repo" stash push -u -q -m "runner: interrupted $slice $ts" >> "$logs/runner.log" 2>&1; then
    pause environment "dirty tree on ${br:-a detached HEAD}, and the stash failed" "$slice"; return 1
  fi
  log "stashed uncommitted changes on ${br:-a detached HEAD} before $kind $slice"
  comment "$slice" "@$OWNER the runner found uncommitted changes on $(sanitize "${br:-a detached HEAD}") before a $kind run of slice #$slice and stashed them as \"runner: interrupted $slice $ts\" (git stash list on the Mac mini). This should not happen; nothing was lost." \
    || log "could not mention the stash on #$slice"
  pause environment "dirty tree on ${br:-a detached HEAD}, stashed" "$slice"
  return 1
}

last_ok_item=""
last_slice=""
claude_item() { # kind pr slice
  local kind="$1" pr="$2" slice="$3" item prompt logf n start rc secs cls why last v vk vs head
  item="$kind $slice"; [ "$pr" = - ] || item="$kind $pr $slice"
  if [ "$(failures "$slice")" -ge 2 ]; then block_slice "$slice" "$pr" "$item" "the earlier Blocked did not go through"; return; fi
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
    with_timeout "$timeout_secs" "$logf" claude -p "Use the reviewer agent on pull request $pr" --permission-mode auto
  else
    with_timeout "$timeout_secs" "$logf" env PATH="$guard_dir:$PATH" \
      claude -p "/pick-up-slice $slice" --permission-mode auto --disallowedTools "${DENY[@]}"
  fi
  rc=$?
  secs=$(( $(now) - start ))
  cls="$(classify_run "$rc" "$secs" "$logf")"
  last="$(grep -v '^[[:space:]]*$' "$logf" 2> /dev/null | tail -n1)"
  why="exit $rc after $secs s: $last"; [ "$rc" = 124 ] && why="timed out after $secs s"

  if [ "$kind" = review ] && [ "$cls" = ok ]; then
    v="$(bash "$scripts/verdict.sh" "$pr" 2>> "$logs/runner.log")" || { pause transient "verdict.sh failed after the review of #$pr" "$slice"; return; }
    head="$(gh pr view "$pr" --repo "$REPO_SLUG" --json headRefOid --jq .headRefOid 2>> "$logs/runner.log")" \
      || { pause transient "could not read the head of #$pr" "$slice"; return; }
    read -r vk vs _ <<<"$v"
    if [ "$vk" = none ] || [ "$vs" != "$head" ]; then
      log "the review of #$pr left no verdict for head $head"
      cls="$(classify_run 1 "$secs" "$logf")"
      why="the review left no verdict for head $head"
    fi
  fi
  log "claude for $item: $cls ($why)"

  case "$cls" in
    ok)
      if [ "$item" = "$last_ok_item" ]; then
        count_failure "$slice" "$pr" "$item" "no progress: the same item came back after an ok run"
      else
        reset_failures "$slice"
      fi
      last_ok_item="$item" ;;
    task) last_ok_item=""; count_failure "$slice" "$pr" "$item" "$why" ;;
    *) pause "$cls" "$why" "$slice" "$logf" ;;
  esac
}

# --- Merge, cap, idle --------------------------------------------------------------------------------------------
merge_item() { # pr slice
  local pr="$1" slice="$2" out rc reasons joined r owner_only=1 errf="$logs/.gate.err"
  out="$(bash "$scripts/merge-gate.sh" "$pr" 2> "$errf")"; rc=$?
  log_lines merge-gate "$errf"
  case "$rc" in
    0) log "merged #$pr: $out"; reset_failures "$slice" ;;
    3)
      reasons="$(grep -v '^[[:space:]]*$' <<<"$out")"
      if [ "$reasons" = "ci-ok is pending" ]; then log "#$pr waits for ci-ok"; nap "$poll"; return; fi
      [ -n "$reasons" ] || owner_only=0
      while IFS= read -r r; do
        case "$r" in "risk is not low"|"touches protected paths") ;; *) owner_only=0 ;; esac
      done <<<"$reasons"
      joined="$(awk 'NR > 1 { printf ", " } { printf "%s", $0 }' <<<"${reasons:-no reasons printed}")"
      log "the merge gate refused #$pr: $joined"
      if [ "$owner_only" = 1 ]; then
        hand_over "$pr" "$slice" "@$OWNER PR #$pr is ready for you: the merge gate leaves it to the owner ($joined)." \
          || pause transient "could not hand #$pr to the owner" "$slice"
      else
        escalate "refused-$pr" "$slice" "@$OWNER PR #$pr needs you: the merge gate refused it ($joined)." add_label "$pr" \
          || pause transient "could not hand #$pr to the owner" "$slice"
      fi ;;
    *) pause transient "merge-gate.sh exited $rc for #$pr" "$slice" ;;
  esac
}

cap_item() { # pr slice
  hand_over "$1" "$2" "@$OWNER PR #$1 has used up its review rounds: the reviewer asked for changes a fourth time, so it waits for you." \
    || pause transient "could not hand #$1 to the owner" "$2"
}

owner_item() { # pr slice
  local sha
  sha="$(head_of "$1")" && [ -n "$sha" ] || { pause transient "could not read the head of #$1" "$2"; return; }
  if told "$1" "$sha"; then log "#$1 still waits for the owner at $sha"; nap "$poll"; return; fi
  tell_owner "$1" "$sha" "$2" "@$OWNER the reviewed PR #$1 is ready for you to read and merge (head $sha)." \
    || pause transient "could not tell the owner about #$1" "$2"
}

idle() {
  local target
  if [ ! -e "$logs/idle" ]; then
    target="$(merged_slice)"
    if [ -z "$target" ]; then
      log "idle: the queue is empty, and no merged slice to mention the owner on"; : > "$logs/idle"
    elif comment "$target" "@$OWNER the runner's queue is empty: no pull request waits on it and no slice is Ready. It looks again every $poll s."; then
      log "idle: the queue is empty, mentioned the owner on #$target"; : > "$logs/idle"
    else
      log "idle: could not mention the owner on #$target"
    fi
  fi
  nap "$poll"
}

# --- Lock and main loop ------------------------------------------------------------------------------------------
mkdir -p "$logs/failures" "$logs/mentioned" || { echo "run.sh: cannot create $logs" >&2; exit 1; }
if ! ( set -C; echo $$ > "$lock" ) 2> /dev/null; then
  holder="$(cat "$lock" 2> /dev/null)"
  if number "$holder" && ps -p "$holder" > /dev/null 2>&1; then
    echo "run.sh: already running (pid $holder holds $lock)" >&2; exit 2
  fi
  rm -f "$lock"
  ( set -C; echo $$ > "$lock" ) 2> /dev/null || { echo "run.sh: could not take $lock" >&2; exit 2; }
  log "took over a stale lock (pid ${holder:-none})"
fi
guard_dir="$(mktemp -d)"
iter_dir=""
cleanup() {
  [ "$(cat "$lock" 2> /dev/null)" = "$$" ] && rm -f "$lock"
  rm -rf "$guard_dir"
  drop_scripts
}
trap cleanup EXIT
trap '[ -n "$child_pid" ] && kill -TERM -- "-$child_pid" 2> /dev/null; log "stopped by a signal"; exit 130' INT TERM
cd "$repo" || { log "cannot enter $repo"; exit 1; }
log "runner started (pid $$, from $0)"

# load_scripts: fetches origin/main and points $scripts at its scripts/agent (or the override); pauses on failure.
load_scripts() {
  git -C "$repo" fetch -q origin main >> "$logs/runner.log" 2>&1 || { pause transient "git fetch origin main failed" ""; return 1; }
  if [ -n "$scripts_override" ]; then scripts="$scripts_override"; return 0; fi
  iter_dir="$(mktemp -d)" || { pause transient "cannot make a temp dir" ""; return 1; }
  if ! git -C "$repo" archive origin/main scripts/agent 2>> "$logs/runner.log" | tar -x -C "$iter_dir" 2>> "$logs/runner.log"; then
    pause transient "cannot extract scripts/agent from origin/main" ""; return 1
  fi
  scripts="$iter_dir/scripts/agent"
}
drop_scripts() { [ -n "$iter_dir" ] && rm -rf "$iter_dir"; iter_dir=""; return 0; }
# back_to_main <slice>: a clean tree goes to main at origin/main; a dirty one is left for the next checkpoint.
back_to_main() {
  local st
  st="$(git -C "$repo" status --porcelain 2>&1)" || { pause environment "git status failed: $st" "$1"; return; }
  if [ -n "$st" ]; then log "the tree is dirty on $(git -C "$repo" branch --show-current 2> /dev/null); leaving it"; return; fi
  git -C "$repo" switch -q main >> "$logs/runner.log" 2>&1 && git -C "$repo" merge -q --ff-only origin/main >> "$logs/runner.log" 2>&1 \
    || pause environment "could not bring main up to date with origin/main" "$1"
}

while :; do
  drop_scripts
  stopped && break
  paused=0
  load_scripts || continue
  line="$(bash "$scripts/pending.sh" 2> "$logs/.pending.err")"; prc=$?
  log_lines pending "$logs/.pending.err"
  stopped && break
  if [ "$prc" != 0 ]; then pause transient "pending.sh exited $prc" ""; continue; fi
  if [ -z "$line" ]; then end_episode; last_ok_item=""; idle; continue; fi
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
  log "next: $line"
  case "$kind" in
    new|resume|fix|review) claude_item "$kind" "$pr" "$slice" ;;
    merge) merge_item "$pr" "$slice" ;;
    cap) cap_item "$pr" "$slice" ;;
    owner) owner_item "$pr" "$slice" ;;
  esac
  stopped || back_to_main "$slice"
  [ "$paused" = 0 ] && end_episode
done
log "the stop file exists; stopping"
exit 0
