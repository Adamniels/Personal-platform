#!/usr/bin/env bash
# Offline tests for run.sh. Fakes on PATH stand in for claude, gh, docker, df and date, fake pending.sh,
# merge-gate.sh, board.sh and verdict.sh sit in AGENT_SCRIPTS, and AGENT_SLEEP is a fake sleep, so no test touches
# GitHub, Docker or a real claude. Time is a fake clock in a file: the fake sleep and the fake claude advance it,
# and the fake date reads it, so durations, reset times and pause episodes are tested at their real values without
# waiting. Only the timeout test waits, about a second. Git is real, in a temp repository with a bare origin.
# Run: bash scripts/agent/run.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin" "$tmp/scripts"
S=1800000000   # the fake clock's start: 2027-01-15 08:00:00 UTC

# --- Fakes -----------------------------------------------------------------------------------------------------
# claude: logs its arguments ("|" after each) to claude.log and which gh it would run to claude-gh.log, runs
# $D/claude-hook when there is one (to dirty the tree, or move the PR head), then plays
# the next line of claude.queue, "<exit> <seconds> <output>" (output through printf %b), or "0 300 done" when the
# queue is empty. <seconds> advances the clock. An exit of "hang" advances the clock and then really sleeps.
cat > "$tmp/bin/claude" <<'FAKE'
#!/usr/bin/env bash
for a in "$@"; do printf '%s|' "$a"; done >> "$D/claude.log"; echo >> "$D/claude.log"
g="$(command -v gh)"; if grep -q 'gh-guard' "$g" 2>/dev/null; then echo guard >> "$D/claude-gh.log"; else echo plain >> "$D/claude-gh.log"; fi
if [ -x "$D/claude-hook" ]; then "$D/claude-hook"; fi
line="0 300 done"
if [ -s "$D/claude.queue" ]; then line="$(head -n1 "$D/claude.queue")"; sed -i.bak 1d "$D/claude.queue"; fi
code="${line%% *}"; rest="${line#* }"; secs="${rest%% *}"; out="${rest#* }"
echo $(( $(cat "$D/clock") + secs )) > "$D/clock"
printf '%b\n' "$out"
if [ "$code" = hang ]; then sleep 30 & echo $! > "$D/hang.pid"; wait; exit 0; fi
exit "$code"
FAKE
# gh: logs every call to gh.log; comments go to comments.log as "<clock> <issue> <body>", labels to labels.log.
cat > "$tmp/bin/gh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$D/gh.log"
case "$*" in
  "api rate_limit"*) exit "${FAKE_RATE_EXIT:-0}" ;;
  "issue comment "*)
    [ "${FAKE_COMMENT_EXIT:-0}" = 0 ] || exit 1
    issue="$3" body=""; while [ $# -gt 0 ]; do [ "$1" = --body ] && body="$2"; shift; done
    echo "$(cat "$D/clock") $issue $(printf '%s' "$body" | tr '\n' ' ')" >> "$D/comments.log" ;;
  "pr edit "*"--add-label"*) [ "${FAKE_LABEL_EXIT:-0}" = 0 ] || exit 1; echo "$3 needs-owner" >> "$D/labels.log" ;;
  "pr view "*) if [ -f "$D/head" ]; then cat "$D/head"; else echo "${FAKE_HEAD:-aaaa}"; fi ;;
  "pr list "*) if [ -f "$D/merged.json" ]; then cat "$D/merged.json"; else echo '[]'; fi ;;
  *) echo "unexpected gh call: $*" >&2; exit 9 ;;
esac
FAKE
cat > "$tmp/bin/docker" <<'FAKE'
#!/usr/bin/env bash
echo "docker $*" >> "$D/probe.log"; if [ "${FAKE_DOCKER_HANG:-0}" = 1 ]; then sleep 30; fi; exit "${FAKE_DOCKER_EXIT:-0}"
FAKE
cat > "$tmp/bin/df" <<'FAKE'
#!/usr/bin/env bash
echo "Filesystem 1024-blocks Used Available Capacity Mounted"; echo "/dev/x 999999999 1 ${FAKE_DF_KB:-100000000} 1% /"
FAKE
# git: the real git, except that fetch hangs while $D/git-hang exists.
real_git="$(command -v git)"
cat > "$tmp/bin/git" <<FAKE
#!/usr/bin/env bash
case " \$* " in *" fetch "*) if [ -e "\$D/git-hang" ]; then sleep 30; fi ;; esac
exec "$real_git" "\$@"
FAKE
# date: "+%s" and "+%s %H %M %S" come from the clock (UTC); any other format is the real date.
cat > "$tmp/bin/date" <<'FAKE'
#!/usr/bin/env bash
c="$(cat "$D/clock")"; d=$((c % 86400))
case "${1:-}" in
  +%s) echo "$c" ;;
  "+%s %H %M %S") printf '%s %02d %02d %02d\n' "$c" $((d / 3600)) $((d % 3600 / 60)) $((d % 60)) ;;
  *) exec /bin/date "$@" ;;
esac
FAKE
# sleep (AGENT_SLEEP): logs its argument, advances the clock, and creates the stop file once the clock reaches
# FAKE_STOP_AT.
cat > "$tmp/bin/fakesleep" <<'FAKE'
#!/usr/bin/env bash
echo "$1" >> "$D/sleep.log"
c=$(( $(cat "$D/clock") + $1 )); echo "$c" > "$D/clock"
if [ -n "${FAKE_STOP_AT:-}" ] && [ "$c" -ge "$FAKE_STOP_AT" ]; then : > "$AGENT_HOME/.agent-stop"; fi
FAKE
# pending.sh: prints the next line of pending.queue. EXIT1 fails like a GitHub error, IDLE prints nothing, HOOK
# runs $D/pending-hook and prints nothing, STOP
# creates the stop file and prints nothing, "REPEAT <line>" prints <line> without consuming it. An empty queue, or
# the 400th call, stops the loop, so no test can spin forever.
cat > "$tmp/scripts/pending.sh" <<'FAKE'
#!/usr/bin/env bash
n=$(( $(cat "$D/pending.count" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$D/pending.count"
line="STOP"; [ -s "$D/pending.queue" ] && line="$(head -n1 "$D/pending.queue")"
[ "$n" -ge 400 ] && line=STOP
case "$line" in
  "REPEAT "*) echo "${line#REPEAT }" ;;
  *)
    sed -i.bak 1d "$D/pending.queue" 2>/dev/null
    case "$line" in
      EXIT1) echo "HTTP 502: Bad Gateway" >&2; exit 1 ;;
      IDLE) ;;
      HOOK) "$D/pending-hook" ;;
      STOP) : > "$AGENT_HOME/.agent-stop" ;;
      *) echo "$line" ;;
    esac ;;
esac
FAKE
cat > "$tmp/scripts/merge-gate.sh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$D/gate.log"; printf '%b' "${FAKE_GATE_OUT:-merged #$1\n}"; exit "${FAKE_GATE_EXIT:-0}"
FAKE
# board.sh: `get <n>` prints status-<n> (default "In review"); `status <n> <s>` logs to board.log and records <s>.
cat > "$tmp/scripts/board.sh" <<'FAKE'
#!/usr/bin/env bash
if [ "$1" = get ]; then if [ -f "$D/status-$2" ]; then cat "$D/status-$2"; else echo "In review"; fi; exit 0; fi
echo "$*" >> "$D/board.log"; [ "${FAKE_BOARD_EXIT:-0}" = 0 ] || exit "$FAKE_BOARD_EXIT"; echo "$3" > "$D/status-$2"
FAKE
cat > "$tmp/scripts/verdict.sh" <<'FAKE'
#!/usr/bin/env bash
echo "${FAKE_VERDICT:-approve aaaa 0 2027-01-15T08:00:00Z}"
FAKE
cp "$here/gh-guard.sh" "$tmp/scripts/gh-guard.sh"
chmod +x "$tmp/bin/"* "$tmp/scripts/"*
export PATH="$tmp/bin:$PATH" AGENT_SCRIPTS="$tmp/scripts" AGENT_SLEEP="$tmp/bin/fakesleep" TZ=UTC
for c in claude gh docker df date; do
  [ "$(command -v "$c")" = "$tmp/bin/$c" ] || { echo "the fake $c is not first on PATH" >&2; exit 1; }
done

# Git sees only this config, never the machine's (a signing key, hooks, another default branch).
printf '[init]\n\tdefaultBranch = main\n[user]\n\tname = t\n\temail = t@example.com\n[commit]\n\tgpgsign = false\n[advice]\n\tdetachedHead = false\n' > "$tmp/gitconfig"
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL="$tmp/gitconfig"

pass=0 fail=0
check() { if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi; }
t=0
# setup: a fresh home, data dir, clock and repository (main holding this run.sh at scripts/agent/run.sh, so the
# runner can start from main's copy, and branch 41-x, both pushed to a bare origin that AGENT_REMOTE names), and
# every fake knob back to its default.
setup() {
  t=$((t + 1))
  D="$tmp/t$t/data"; AGENT_HOME="$tmp/t$t/home"; AGENT_REPO="$tmp/t$t/repo"
  mkdir -p "$D" "$AGENT_HOME"
  echo "$S" > "$D/clock"
  : > "$D/claude.queue"; : > "$D/pending.queue"
  git init -q --bare -b main "$tmp/t$t/origin.git"
  git init -q -b main "$AGENT_REPO"
  mkdir -p "$AGENT_REPO/scripts/agent"; cp "$here/run.sh" "$AGENT_REPO/scripts/agent/run.sh"
  echo base > "$AGENT_REPO/file"; git -C "$AGENT_REPO" add file scripts; git -C "$AGENT_REPO" commit -q -m base
  git -C "$AGENT_REPO" remote add origin "$tmp/t$t/origin.git"
  git -C "$AGENT_REPO" branch 41-x
  git -C "$AGENT_REPO" push -q origin main 41-x 2>/dev/null
  export D AGENT_HOME AGENT_REPO AGENT_POLL_SECONDS=0 AGENT_TIMEOUT_SECONDS=5400 AGENT_REMOTE="$tmp/t$t/origin.git"
  unset FAKE_STOP_AT FAKE_RATE_EXIT FAKE_COMMENT_EXIT FAKE_LABEL_EXIT FAKE_HEAD FAKE_DOCKER_EXIT FAKE_DF_KB \
        FAKE_GATE_OUT FAKE_GATE_EXIT FAKE_BOARD_EXIT FAKE_VERDICT FAKE_DOCKER_HANG AGENT_CALL_TIMEOUT_SECONDS
}
pending() { printf '%s\n' "$@" > "$D/pending.queue"; }
claude_plays() { printf '%s\n' "$@" > "$D/claude.queue"; }
runner() { bash "$here/run.sh" > "$D/out" 2>&1; rc=$?; }
again() { rm -f "$AGENT_HOME/.agent-stop" "$D/pending.count"; }   # before a second runner on the same home
hook() { printf '#!/usr/bin/env bash\n%s\n' "$2" > "$D/$1"; chmod +x "$D/$1"; }
lines() { if [ -f "$1" ]; then grep -c '' "$1"; else echo 0; fi; }
sleeps() { if [ -f "$D/sleep.log" ]; then awk '{ s += $1 } END { print s + 0 }' "$D/sleep.log"; else echo 0; fi; }
no_file() { [ ! -e "$1" ]; }
# waits <kind>: the pause lengths run.sh logged for that kind, in order, space separated.
waits() { sed -n "s/.*pausing ($1: .*) for \([0-9]*\) s\$/\1/p" "$AGENT_HOME/agent-logs/runner.log" | tr '\n' ' ' | sed 's/ $//'; }
fails() { cat "$AGENT_HOME/agent-logs/failures/$1" 2>/dev/null || echo 0; }

# --- Work items ------------------------------------------------------------------------------------------------
setup; pending "new 41"; runner
check "new 41 runs claude -p /pick-up-slice 41 in auto mode" \
  $(grep -q '^-p|/pick-up-slice 41|--permission-mode|auto|--disallowedTools|' "$D/claude.log"; echo $?)
check "an implementer run blocks Bash commands with verdict:" $(grep -qF '|Bash(*verdict:*)|' "$D/claude.log"; echo $?)
check "an implementer run blocks PATCH and DELETE through gh api" \
  $(grep -qF '|Bash(*gh api *-X PATCH*)|' "$D/claude.log" && grep -qF '|Bash(*gh api *--method DELETE*)|' "$D/claude.log"; echo $?)
check "an implementer run blocks the merge gate" $(grep -qF '|Bash(*merge-gate.sh*)|' "$D/claude.log"; echo $?)
check "an implementer run sees the gh guard first on PATH (R36)" $([ "$(cat "$D/claude-gh.log")" = guard ]; echo $?)
check "the stop file ends the loop with exit 0" $([ "$rc" = 0 ]; echo $?)
check "each claude call logs to agent-logs/<slice>-<timestamp>.log" \
  $(ls "$AGENT_HOME/agent-logs/" | grep -Eq '^41-[0-9]{8}-[0-9]{6}\.log$' && grep -q done "$AGENT_HOME"/agent-logs/41-*.log; echo $?)
check "the runner's decisions go to runner.log with timestamps" \
  $(grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9:]{8} .*new 41' "$AGENT_HOME/agent-logs/runner.log"; echo $?)

setup; pending "resume 41" "fix 50 41"; runner
check "resume and fix run /pick-up-slice <slice> with the guard" \
  $([ "$(grep -c '^-p|/pick-up-slice 41|--permission-mode|auto|--disallowedTools|' "$D/claude.log")" = 2 ] \
    && [ "$(grep -c guard "$D/claude-gh.log")" = 2 ]; echo $?)

setup; pending "review 50 41"; runner
check "review runs the reviewer agent on the PR, without the implementer restrictions or the guard" \
  $([ "$(cat "$D/claude.log")" = "-p|Use the reviewer agent on pull request 50|--permission-mode|auto|" ] \
    && [ "$(cat "$D/claude-gh.log")" = plain ]; echo $?)

setup; pending "merge 50 41"; runner
check "merge 50 41 calls the merge gate with 50, and no claude" \
  $([ "$(cat "$D/gate.log")" = 50 ] && no_file "$D/claude.log"; echo $?)

setup; export AGENT_POLL_SECONDS=7 FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok is pending\n'; pending "merge 50 41"; runner
check "merge exit 3 with only ci-ok pending sleeps the poll interval, no label, no mention" \
  $([ "$(sleeps)" = 7 ] && no_file "$D/labels.log" && no_file "$D/comments.log" && no_file "$D/board.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='touches protected paths\nrisk is not low\n'; pending "merge 50 41"; runner
check "merge exit 3 with only owner reasons adds needs-owner and says PR #50 is ready, once, on the slice" \
  $([ "$(cat "$D/labels.log")" = "50 needs-owner" ] && [ "$(lines "$D/comments.log")" = 1 ] \
    && grep -q "^$S 41 @Adamniels PR #50 is ready for you" "$D/comments.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok failed\nrisk is not low\n'; pending "merge 50 41"; runner
check "merge exit 3 with other reasons adds needs-owner and mentions the reasons" \
  $([ "$(cat "$D/labels.log")" = "50 needs-owner" ] && grep -q '@Adamniels PR #50 .*ci-ok failed.*risk is not low' "$D/comments.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='risk is not low\n' FAKE_LABEL_EXIT=1; pending "merge 50 41" "merge 50 41"; runner
check "a failed label is retried without a second mention" \
  $([ "$(lines "$D/comments.log")" = 1 ] && [ "$(grep -c 'add-label' "$D/gh.log")" = 2 ]; echo $?)

setup; export FAKE_GATE_EXIT=1; pending "merge 50 41"; runner
check "merge exit 1 is a transient pause: 60 s, no label, no mention, no count" \
  $([ "$(sleeps)" = 60 ] && no_file "$D/labels.log" && no_file "$D/comments.log" && [ "$(fails 41)" = 0 ]; echo $?)

setup; pending "cap 50 41"; runner
check "cap 50 41 adds needs-owner and mentions the owner that review rounds ran out" \
  $([ "$(cat "$D/labels.log")" = "50 needs-owner" ] && grep -q '^[0-9]* 41 @Adamniels PR #50 .*review rounds' "$D/comments.log"; echo $?)

# --- Owner items: one mention per PR and head sha (R42) ---------------------------------------------------------
setup; export AGENT_POLL_SECONDS=5; pending "owner 50 41" "owner 50 41"; runner
check "an owner item mentions once that the reviewed PR is ready to read and merge, and records pr and sha" \
  $([ "$(lines "$D/comments.log")" = 1 ] && grep -q ' 41 @Adamniels the reviewed PR #50 is ready for you to read and merge' "$D/comments.log" \
    && [ "$(cat "$AGENT_HOME/agent-logs/owner-told")" = "50 aaaa" ]; echo $?)
check "the same pr and sha again comments nothing and waits the poll interval" $([ "$(sleeps)" = 5 ]; echo $?)
rm -f "$AGENT_HOME/.agent-stop"; export FAKE_HEAD=bbbb; pending "owner 50 41"; runner
check "a new head sha gets one new mention" $([ "$(lines "$D/comments.log")" = 2 ] && grep -q 'head bbbb' "$D/comments.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='risk is not low\n'; pending "merge 50 41" "owner 50 41"; runner
check "a merge-gate ready mention, then an owner item for the same sha, mentions once" $([ "$(lines "$D/comments.log")" = 1 ]; echo $?)

setup; pending "cap 50 41" "owner 50 41"; runner
check "a cap mention, then an owner item for the same sha, mentions once" $([ "$(lines "$D/comments.log")" = 1 ]; echo $?)

# --- Failures count only when they are about the slice (R13) ----------------------------------------------------
setup; pending "new 41" "new 41"; claude_plays "1 200 tests fail" "1 200 tests fail"; runner
check "two slow failing runs set Blocked and mention the owner once" \
  $([ "$(cat "$D/board.log")" = "status 41 Blocked" ] && [ "$(lines "$D/comments.log")" = 1 ] \
    && grep -q ' 41 @Adamniels slice #41 is Blocked' "$D/comments.log"; echo $?)

setup; pending "new 41" "new 41"; claude_plays "1 200 tests fail" "0 300 done"; runner
check "one slow failure then a success does not set Blocked and resets the count" \
  $(no_file "$D/board.log" && [ "$(fails 41)" = 0 ]; echo $?)

setup; pending "new 41" "new 41"; claude_plays "1 10 something broke" "1 10 something broke"; runner
check "a fast unknown failure is transient: no count, no Blocked, backoff 60 then 120" \
  $(no_file "$D/board.log" && [ "$(fails 41)" = 0 ] && [ "$(waits transient)" = "60 120" ]; echo $?)

setup; export AGENT_TIMEOUT_SECONDS=1; pending "new 41" "new 41"; claude_plays "hang 200 working" "hang 200 working"
began=$(/bin/date +%s); runner; took=$(( $(/bin/date +%s) - began ))
check "a claude past the timeout is killed, counted, and the second sets Blocked" \
  $([ "$(cat "$D/board.log")" = "status 41 Blocked" ] && [ "$took" -lt 20 ]; echo $?)
check "the timeout reaps the process group (claude's child is gone)" $(! kill -0 "$(cat "$D/hang.pid")" 2>/dev/null; echo $?)

setup; pending "new 41" "new 41"; claude_plays "0 300 working\nStopped: no mode matches" "0 20 x\nBlocked: no Acceptance"; runner
check "Stopped: and Blocked: last lines are task failures whatever the exit and duration (R35)" \
  $(grep -q 'status 41 Blocked' "$D/board.log"; echo $?)

setup; pending "new 41"; claude_plays "0 300 working\nBlocked: no Acceptance"; runner
check "one Blocked: run counts one failure" $([ "$(fails 41)" = 1 ]; echo $?)

setup; pending "new 41" "new 41" "new 41"; claude_plays "0 300 done" "0 300 done" "0 300 done"; runner
check "the same item after three ok runs is no progress and sets Blocked" $(grep -q 'status 41 Blocked' "$D/board.log"; echo $?)

# --- Silent reviews (R32) ---------------------------------------------------------------------------------------
setup; export FAKE_VERDICT="none 0 0 -"; pending "review 50 41" "review 50 41"; runner
check "two slow reviews that leave no verdict set Blocked and add needs-owner to the PR" \
  $(grep -q 'status 41 Blocked' "$D/board.log" && grep -q '50 needs-owner' "$D/labels.log"; echo $?)

setup; export FAKE_VERDICT="approve bbbb 0 2027-01-15T08:00:00Z" FAKE_HEAD=aaaa; pending "review 50 41"; runner
check "a verdict for an older head counts as a silent review" $([ "$(fails 41)" = 1 ]; echo $?)

setup; export FAKE_VERDICT="none 0 0 -"; pending "review 50 41" "review 50 41"; claude_plays "0 10 x" "0 10 x"; runner
check "a fast silent review is transient, not counted" $(no_file "$D/board.log" && [ "$(fails 41)" = 0 ]; echo $?)

setup; pending "review 50 41"; runner
check "a review that leaves a verdict for the head is ok" $([ "$(fails 41)" = 0 ] && no_file "$D/board.log"; echo $?)

# --- Pauses (R14, R15) ------------------------------------------------------------------------------------------
setup; pending "new 41" "new 41" "new 41"
claude_plays "1 5 Claude AI usage limit reached|$((S + 3600))" "1 5 Claude AI usage limit reached|$((S + 3600))" "0 300 done"; runner
check "a usage limit sleeps until the reset plus 60 s (at least 60 s once past it) and retries the same item" \
  $([ "$(waits limit)" = "3655 60" ] && [ "$(grep -c '^-p|/pick-up-slice 41|' "$D/claude.log")" = 3 ]; echo $?)
check "a usage limit never counts, never sets Blocked, never mentions" \
  $([ "$(fails 41)" = 0 ] && no_file "$D/board.log" && no_file "$D/comments.log"; echo $?)

setup; pending "new 41" "new 41"; claude_plays "1 5 You have hit your limit · resets 3pm (Europe/Stockholm)" "0 300 done"; runner
check "a reset clock time (3pm, clock at 08:00:05) sleeps until 15:01" $([ "$(sleeps)" = 25255 ]; echo $?)

setup; pending "new 41" "new 41"; claude_plays "1 5 Error: rate_limit_error" "0 300 done"; runner
check "a usage limit without a reset time sleeps 15 min" $([ "$(sleeps)" = 900 ]; echo $?)

setup; export FAKE_STOP_AT=$((S + 4 * 3600)); pending "REPEAT new 41"; for i in 1 2 3 4 5 6 7 8; do echo "1 5 usage limit reached"; done > "$D/claude.queue"; runner
check "limits for hours: backoff 15, 30, 60, 60 min, still no count, no Blocked, no mention" \
  $([ "$(waits limit | cut -d' ' -f1-5)" = "900 1800 3600 3600 3600" ] && [ "$(fails 41)" = 0 ] && no_file "$D/board.log" \
    && no_file "$D/comments.log"; echo $?)

setup; pending "EXIT1" "STOP"; runner
check "pending.sh exit 1 is a transient pause with no claude call" $([ "$(sleeps)" = 60 ] && no_file "$D/claude.log"; echo $?)
check "pending.sh's stderr reaches runner.log" $(grep -q 'HTTP 502: Bad Gateway' "$AGENT_HOME/agent-logs/runner.log"; echo $?)

setup; export FAKE_DOCKER_EXIT=1 FAKE_STOP_AT=$((S + 3 * 3600)); pending "REPEAT new 41"; runner
check "a failed docker probe pauses with no claude call and no board change" $(no_file "$D/claude.log" && no_file "$D/board.log"; echo $?)
check "an environment episode mentions the owner once, only after 30 min" \
  $([ "$(lines "$D/comments.log")" = 1 ] && [ $(( $(cut -d' ' -f1 "$D/comments.log") - S )) -gt 1800 ] \
    && grep -q ' 41 @Adamniels' "$D/comments.log"; echo $?)
check "environment backoff is 1 min doubling to 30 min" \
  $([ "$(waits environment | cut -d' ' -f1-8)" = "60 120 240 480 960 1800 1800 1800" ]; echo $?)

setup; export FAKE_RATE_EXIT=1; pending "new 41" "STOP"; runner
check "a failed gh probe pauses with no claude call" $(no_file "$D/claude.log" && [ "$(sleeps)" = 60 ]; echo $?)

setup; export FAKE_DF_KB=4000000; pending "new 41" "STOP"; runner
check "free disk under 5 GB pauses with no claude call" $(no_file "$D/claude.log" && [ "$(sleeps)" = 60 ]; echo $?)

setup; pending "new 41"; claude_plays "0 300 checked\nENVIRONMENT: docker is not running"; runner
check "an ENVIRONMENT: last line pauses, no Blocked, no count" \
  $([ "$(sleeps)" = 60 ] && no_file "$D/board.log" && [ "$(fails 41)" = 0 ]; echo $?)

setup; export FAKE_STOP_AT=$((S + 600)); pending "REPEAT new 41"; claude_plays "1 5 usage limit reached|$((S + 7200))"; runner
check "the stop file during a pause ends the loop with exit 0, in 30 s chunks" \
  $([ "$rc" = 0 ] && [ "$(lines "$D/claude.log")" = 1 ] && [ "$(sleeps)" = 600 ] && ! grep -qv '^30$' "$D/sleep.log"; echo $?)

# --- Interrupted work (R16) -------------------------------------------------------------------------------------
setup; git -C "$AGENT_REPO" switch -q 41-x; echo half > "$AGENT_REPO/file"; echo new > "$AGENT_REPO/untracked"
pending "resume 41"; runner
check "a dirty tree on the slice's branch is committed as a checkpoint before resume" \
  $([ "$(git -C "$AGENT_REPO" log -1 --format=%s 41-x)" = "Checkpoint: interrupted run" ] \
    && [ -z "$(git -C "$AGENT_REPO" status --porcelain)" ] && [ "$(lines "$D/claude.log")" = 1 ]; echo $?)
check "the checkpoint is not pushed" \
  $([ "$(git -C "$tmp/t$t/origin.git" rev-parse 41-x)" = "$(git -C "$AGENT_REPO" rev-parse main)" ]; echo $?)

setup; echo stray > "$AGENT_REPO/file"; pending "resume 41" "resume 41"; runner
check "a dirty tree on main is stashed with a mention, and no claude call until it is clean" \
  $(git -C "$AGENT_REPO" stash list | grep -q 'runner: interrupted 41' && [ "$(lines "$D/comments.log")" = 1 ] \
    && [ "$(lines "$D/claude.log")" = 1 ] && [ "$(head -n1 "$D/sleep.log")" = 30 ]; echo $?)

# --- What runs is main's (R43) ---------------------------------------------------------------------------------
# The fake scripts are committed to main and pushed; branch 41-x, checked out, carries booby-trapped copies that
# log to trap.log. With AGENT_SCRIPTS unset the runner must call the ones extracted from origin/main.
setup; unset AGENT_SCRIPTS
mkdir -p "$AGENT_REPO/scripts/agent"; cp "$tmp/scripts/"* "$AGENT_REPO/scripts/agent/"
git -C "$AGENT_REPO" add scripts; git -C "$AGENT_REPO" commit -q -m scripts; git -C "$AGENT_REPO" push -q origin main 2>/dev/null
git -C "$AGENT_REPO" switch -q 41-x; mkdir -p "$AGENT_REPO/scripts/agent"
for f in pending.sh merge-gate.sh verdict.sh board.sh gh-guard.sh; do
  printf '#!/usr/bin/env bash\necho "%s $*" >> "$D/trap.log"; : > "$AGENT_HOME/.agent-stop"\n' "$f" > "$AGENT_REPO/scripts/agent/$f"
done
chmod +x "$AGENT_REPO/scripts/agent/"*; git -C "$AGENT_REPO" add scripts; git -C "$AGENT_REPO" commit -q -m trap
pending "merge 50 41" "review 50 41" "new 41"; runner
check "pending, the gate, verdict and the guard come from origin/main, never the checked-out branch" \
  $(no_file "$D/trap.log" && [ "$(cat "$D/gate.log")" = 50 ] && [ "$(lines "$D/claude.log")" = 2 ] \
    && [ "$(tail -n1 "$D/claude-gh.log")" = guard ]; echo $?)
check "the extracted scripts are removed" $(! ls -d "${TMPDIR:-/tmp}"/tmp.* 2>/dev/null | while read -r x; do [ -e "$x/scripts/agent/pending.sh" ] && echo "$x"; done | grep -q .; echo $?)
export AGENT_SCRIPTS="$tmp/scripts"

setup; pending "new 41"; runner
check "the runner runs from its copy in AGENT_HOME/runner" \
  $(cmp -s "$here/run.sh" "$AGENT_HOME/runner/run.sh" && grep -q "from $AGENT_HOME/runner/run.sh" "$AGENT_HOME/agent-logs/runner.log"; echo $?)

setup; hook pending-hook "mv '$tmp/t$t/origin.git' '$tmp/t$t/moved.git'"; export FAKE_STOP_AT=$((S + 60)); pending HOOK "new 41"; runner
check "a failed fetch is a transient pause with no pending.sh and no claude call" \
  $(no_file "$D/claude.log" && [ "$(cat "$D/pending.count")" = 1 ] && [ "$(waits transient)" = 60 ]; echo $?)

setup; export AGENT_REMOTE="$tmp/t$t/missing.git"; pending "new 41"; runner
check "the runner refuses to start when main cannot be fetched (R48)" $([ "$rc" = 1 ] && no_file "$D/pending.count"; echo $?)

setup; hook pending-hook "cd '$tmp/t$t/repo' && git switch -q main && echo '# v2' >> scripts/agent/run.sh && git commit -q -am v2 && git push -q origin main 2>/dev/null"
pending HOOK "new 41"; runner
check "a new run.sh on main restarts the runner from it (R48)" \
  $(grep -q 'restarting from it' "$AGENT_HOME/agent-logs/runner.log" && [ "$(tail -n1 "$AGENT_HOME/runner/run.sh")" = '# v2' ] \
    && [ "$(grep -c 'runner started' "$AGENT_HOME/agent-logs/runner.log")" = 2 ] && [ "$(lines "$D/claude.log")" = 1 ]; echo $?)

setup; git clone -q "$tmp/t$t/origin.git" "$tmp/t$t/other" 2>/dev/null
git -C "$tmp/t$t/other" commit -q --allow-empty -m ahead; git -C "$tmp/t$t/other" push -q origin main 2>/dev/null
git -C "$AGENT_REPO" switch -q 41-x; pending "merge 50 41"; runner
check "after an item a clean tree is switched to main and fast-forwarded to origin/main" \
  $([ "$(git -C "$AGENT_REPO" branch --show-current)" = main ] && [ "$(git -C "$AGENT_REPO" log -1 --format=%s)" = ahead ]; echo $?)

setup; git -C "$AGENT_REPO" switch -q 41-x; echo half > "$AGENT_REPO/file"; pending "merge 50 41"; runner
check "after an item a dirty tree is left where it is" \
  $([ "$(git -C "$AGENT_REPO" branch --show-current)" = 41-x ] && [ "$(cat "$AGENT_REPO/file")" = half ]; echo $?)

setup; export AGENT_POLL_SECONDS=5; pending "cap 50 41" "cap 50 41" "cap 50 41"; runner
check "a repeated cap for the same pr and sha mentions once and waits the poll interval each time (R43b)" \
  $([ "$(lines "$D/comments.log")" = 1 ] && [ "$(tr '\n' ' ' < "$D/sleep.log")" = "5 5 " ]; echo $?)

# --- Review round 1 ----------------------------------------------------------------------------------------------
setup; pending "new 41"; claude_plays "0 300 opened https://github.com/Adamniels/Personal-platform/pull/429"; runner
check "an ok run whose last line holds 429 is ok, not a limit (I3)" $(no_file "$D/sleep.log" && [ "$(fails 41)" = 0 ]; echo $?)

setup; pending "new 41"; claude_plays "1 300 test_rate_limiting failed: expected 429 got 200"; runner
check "a slow failure that mentions rate limiting in code output is a task failure (I3)" $([ "$(fails 41)" = 1 ]; echo $?)

setup; pending "REPEAT new 41"; for i in 1 2 3 4 5 6 7 8 9 10 11 12; do echo "1 5 Claude AI usage limit reached|$S"; done > "$D/claude.queue"
export FAKE_STOP_AT=$((S + 700)); runner
check "ten limits in a row on the same item mention the owner once (I3 backstop)" \
  $([ "$(lines "$D/comments.log")" = 1 ] && grep -q ' 41 @Adamniels .*usage limit' "$D/comments.log" && no_file "$D/board.log"; echo $?)

setup; export FAKE_STOP_AT=$((S + 7 * 3600)); pending "REPEAT new 41"; for i in 1 2 3 4 5 6 7 8 9; do echo "1 5 usage limit reached"; done > "$D/claude.queue"; runner
check "a limit episode past 6 h mentions the owner once, after 6 h (I3 backstop)" \
  $([ "$(lines "$D/comments.log")" = 1 ] && [ $(( $(cut -d' ' -f1 "$D/comments.log") - S )) -gt 21600 ]; echo $?)

setup; export FAKE_DOCKER_HANG=1 AGENT_CALL_TIMEOUT_SECONDS=1 FAKE_STOP_AT=$((S + 60)); pending "REPEAT new 41"
began=$(/bin/date +%s); runner; took=$(( $(/bin/date +%s) - began ))
check "a hanging docker info times out into an environment pause with no claude call (I4)" \
  $(no_file "$D/claude.log" && [ "$(waits environment)" = 60 ] && [ "$took" -lt 20 ]; echo $?)

setup; hook claude-hook "git -C '$tmp/t$t/repo' switch -q 41-x; echo work > '$tmp/t$t/repo/file'"; pending "new 41"; runner
check "a slice's own run that leaves its branch dirty is checkpointed there, then the tree goes to main (I6)" \
  $([ "$(git -C "$AGENT_REPO" log -1 --format=%s 41-x)" = "Checkpoint: interrupted run" ] \
    && [ "$(git -C "$AGENT_REPO" branch --show-current)" = main ] && [ -z "$(git -C "$AGENT_REPO" status --porcelain)" ] \
    && no_file "$D/comments.log"; echo $?)

setup; hook claude-hook "echo stray > '$tmp/t$t/repo/file'"; pending "new 41"; runner
check "dirt left on another branch after a run is stashed with a mention, never committed (I6)" \
  $(git -C "$AGENT_REPO" stash list | grep -q 'runner: interrupted 41' && [ "$(lines "$D/comments.log")" = 1 ] \
    && [ "$(git -C "$AGENT_REPO" log -1 --format=%s main)" = base ]; echo $?)

setup; printf '#!/bin/sh\nexit 1\n' > "$AGENT_REPO/.git/hooks/pre-commit"; printf '#!/bin/sh\ntouch "%s"\n' "$D/hook-ran" > "$AGENT_REPO/.git/hooks/post-checkout"
chmod +x "$AGENT_REPO/.git/hooks/"*; git -C "$AGENT_REPO" -c core.hooksPath=/dev/null switch -q 41-x; echo half > "$AGENT_REPO/file"
pending "resume 41"; runner
check "the runner's git ignores repository hooks: a failing pre-commit does not stop the checkpoint (R49)" \
  $([ "$(git -C "$AGENT_REPO" log -1 --format=%s 41-x)" = "Checkpoint: interrupted run" ] && no_file "$D/hook-ran"; echo $?)

setup; export AGENT_POLL_SECONDS=7 FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok is pending\nrisk is not low\n'; pending "merge 50 41"; runner
check "ci-ok pending among other reasons waits: no label, no mention (R45)" \
  $([ "$(sleeps)" = 7 ] && no_file "$D/labels.log" && no_file "$D/comments.log"; echo $?)

setup; export AGENT_POLL_SECONDS=600 FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok is pending\n' FAKE_STOP_AT=$((S + 3 * 3600)); pending "REPEAT merge 50 41"; runner
check "ci-ok pending for the same head for 2 h mentions once, after 2 h (R50)" \
  $([ "$(lines "$D/comments.log")" = 1 ] && [ $(( $(cut -d' ' -f1 "$D/comments.log") - S )) -ge 7200 ] \
    && grep -q '@Adamniels ci-ok has been pending on PR #50' "$D/comments.log" && no_file "$D/labels.log"; echo $?)

setup; export AGENT_POLL_SECONDS=5 FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok failed\n'; pending "merge 50 41" "owner 50 41"; runner
check "a refused merge, then an owner item for the same sha, mentions once, never as ready to merge (R42)" \
  $([ "$(lines "$D/comments.log")" = 1 ] && ! grep -q 'ready' "$D/comments.log"; echo $?)

setup; export FAKE_VERDICT="none 0 0 -" AGENT_POLL_SECONDS=5; pending "review 50 41" "review 50 41" "review 50 41" "review 50 41" "cap 50 41"
claude_plays "0 300 SECRET-LOG-TEXT"; runner
check "a Blocked PR item runs no more claude and mentions once, on the PR itself (R47, R54)" \
  $([ "$(lines "$D/claude.log")" = 2 ] && [ "$(lines "$D/comments.log")" = 1 ] && [ "$(grep -c 'status 41 Blocked' "$D/board.log")" = 1 ] \
    && [ "$(cut -d' ' -f2-3 "$D/comments.log")" = "50 @Adamniels" ] \
    && [ "$(tr '\n' ' ' < "$D/sleep.log")" = "5 5 5 " ]; echo $?)
check "the Blocked mention points at the log instead of quoting it (M3)" \
  $(grep -q 'See .*/agent-logs/41-' "$D/comments.log" && ! grep -q 'SECRET-LOG-TEXT' "$D/comments.log"; echo $?)
again; pending "fix 50 41"; runner
check "a fix (owner input) on a Blocked slice sets it In review, clears the record and runs (R51)" \
  $(grep -q 'status 41 In review' "$D/board.log" && [ "$(lines "$D/claude.log")" = 3 ] \
    && ! grep -q '^50 ' "$AGENT_HOME/agent-logs/blocked-prs"; echo $?)

setup; export FAKE_VERDICT="none 0 0 -" AGENT_POLL_SECONDS=5; pending "review 50 41" "review 50 41"; runner
again; echo "In review" > "$D/status-41"; export FAKE_VERDICT="approve aaaa 0 2027-01-15T08:00:00Z"; pending "review 50 41"; runner
check "a slice the owner moved off Blocked runs again at the same head, and its record is cleared (R51)" \
  $([ "$(lines "$D/claude.log")" = 3 ] && ! grep -q '^50 ' "$AGENT_HOME/agent-logs/blocked-prs"; echo $?)

setup; export FAKE_VERDICT="none 0 0 -" AGENT_POLL_SECONDS=5; pending "review 50 41" "review 50 41"; runner
again; echo bbbb > "$D/head"; pending "review 50 41"; runner
check "a new head alone does not unblock: the slice stays Blocked until the owner acts (R51, R54)" \
  $([ "$(lines "$D/claude.log")" = 2 ] && [ "$(tail -n1 "$D/sleep.log")" = 5 ]; echo $?)

# R54 as an interaction: pending.sh's handled-time rule over the comments the runner posts. The fake below offers
# `fix 50 41` only while the owner's last comment (owner-t) is newer than the PR's last handling, which is the newest
# bot comment on PR 50; on the slice it skips a Blocked slice unless that holds (R51). Each owner comment must wake
# the PR exactly once, and each Block must mention exactly once, however many loops run.
mkdir -p "$tmp/scripts-handled"; cp "$tmp/scripts/"* "$tmp/scripts-handled/"
cat > "$tmp/scripts-handled/pending.sh" <<'FAKE'
#!/usr/bin/env bash
n=$(( $(cat "$D/pending.count" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$D/pending.count"
[ "$n" -ge "${FAKE_LOOPS:-30}" ] && { : > "$AGENT_HOME/.agent-stop"; exit 0; }
owner=$(cat "$D/owner-t" 2>/dev/null || echo 0)
handled=$(awk '$2 == 50 { t = $1 } END { print t + 0 }' "$D/comments.log" 2>/dev/null || echo 0)
[ "$owner" -gt "$handled" ] && echo "fix 50 41"
exit 0
FAKE
chmod +x "$tmp/scripts-handled/pending.sh"
setup; export AGENT_SCRIPTS="$tmp/scripts-handled"; echo $((S + 1)) > "$D/owner-t"
for i in 1 2 3 4 5 6; do echo "1 300 tests fail"; done > "$D/claude.queue"; runner
check "R54: an owner comment wakes a failing PR once; the Block on the PR ends the cycle with one mention" \
  $([ "$(lines "$D/claude.log")" = 2 ] && [ "$(lines "$D/comments.log")" = 1 ] && [ "$(grep -c 'status 41 Blocked' "$D/board.log")" = 1 ] \
    && [ "$(cut -d' ' -f2 "$D/comments.log")" = 50 ] && ! grep -q 'In review' "$D/board.log"; echo $?)
again; echo $(( $(cat "$D/clock") + 1 )) > "$D/owner-t"; runner
check "R54: a second owner comment wakes it exactly once more (In review once, two more runs, one more mention)" \
  $([ "$(lines "$D/claude.log")" = 4 ] && [ "$(lines "$D/comments.log")" = 2 ] && [ "$(grep -c 'status 41 In review' "$D/board.log")" = 1 ] \
    && [ "$(grep -c 'status 41 Blocked' "$D/board.log")" = 2 ]; echo $?)
export AGENT_SCRIPTS="$tmp/scripts"

setup; export AGENT_POLL_SECONDS=600 FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok is pending\n' FAKE_STOP_AT=$((S + 3 * 3600)); pending "REPEAT merge 50 41"; runner
again; export FAKE_GATE_OUT='risk is not low\n'; unset FAKE_STOP_AT; pending "merge 50 41"; runner
check "a ci-ok wait mention does not suppress the later ready mention for the same head (R52)" \
  $([ "$(lines "$D/comments.log")" = 2 ] && grep -q 'PR #50 is ready for you' "$D/comments.log" && grep -qx '50 aaaa:ci' "$AGENT_HOME/agent-logs/owner-told"; echo $?)

setup; git -C "$AGENT_REPO" switch -q 41-x; pending "review 50 41"; runner
check "a review needs a clean tree on main: on another branch it is an environment pause with no claude (R53)" \
  $(no_file "$D/claude.log" && [ "$(waits environment)" = 60 ]; echo $?)

setup; echo stray > "$AGENT_REPO/file"; pending "review 50 41"; runner
check "a review on a dirty main is an environment pause with no claude (R53)" $(no_file "$D/claude.log" && [ "$(waits environment)" = 60 ]; echo $?)

setup; hook pending-hook "cd '$tmp/t$t/repo' && git switch -q main && echo '# v2' >> scripts/agent/run.sh && git commit -q -am v2 && git push -q origin main 2>/dev/null; chmod 555 '$AGENT_HOME/runner'"
pending HOOK IDLE "new 41"; runner; chmod 755 "$AGENT_HOME/runner"
check "a re-exec whose copy cannot be moved logs once and keeps running the old copy" \
  $([ "$(grep -c 'could not install' "$AGENT_HOME/agent-logs/runner.log")" = 1 ] && [ "$(grep -c 'runner started' "$AGENT_HOME/agent-logs/runner.log")" = 1 ] \
    && [ "$(lines "$D/claude.log")" = 1 ]; echo $?)

setup; export AGENT_CALL_TIMEOUT_SECONDS=1 FAKE_STOP_AT=$((S + 60)); hook pending-hook ": > '$D/git-hang'"; pending HOOK "new 41"; runner
check "a hanging fetch times out into a transient pause, logged with the git verb" \
  $(grep -q 'git fetch timed out after 1 s' "$AGENT_HOME/agent-logs/runner.log" && [ "$(waits transient)" = 60 ] && no_file "$D/claude.log"; echo $?)

setup; pending "new 41"; runner; again; pending "new 41"; runner; again; pending "new 41"; runner
check "the no-progress guard survives a restart (M4)" $(grep -q 'status 41 Blocked' "$D/board.log"; echo $?)

setup; hook claude-hook "n=\$(( \$(cat '$D/n' 2>/dev/null || echo 0) + 1 )); echo \$n > '$D/n'; echo a\$n > '$D/head'"
pending "fix 50 41" "fix 50 41" "fix 50 41"; runner
check "the same fix item at a new head each time is progress, not counted (M4)" $([ "$(fails 41)" = 0 ] && no_file "$D/board.log"; echo $?)

# --- Lock and idle (R18) ----------------------------------------------------------------------------------------
lockwith() { mkdir -p "$AGENT_HOME/agent-logs/run.lock"; echo "$1" > "$AGENT_HOME/agent-logs/run.lock/pid"; }
setup; mkdir "$tmp/t$t/other-runner"; printf 'sleep 30\n' > "$tmp/t$t/other-runner/run.sh"
bash "$tmp/t$t/other-runner/run.sh" & live=$!; lockwith "$live"; pending "new 41"; runner; kill "$live" 2>/dev/null
check "a live lock held by a run.sh exits 2, already running, without asking pending.sh" \
  $([ "$rc" = 2 ] && grep -q 'already running' "$D/out" && no_file "$D/pending.count"; echo $?)

setup; sh -c 'exit 0' & dead=$!; wait "$dead"; lockwith "$dead"; pending "new 41"; runner
check "a stale lock is taken over, and released at exit" \
  $([ "$rc" = 0 ] && [ "$(lines "$D/claude.log")" = 1 ] && no_file "$AGENT_HOME/agent-logs/run.lock"; echo $?)

setup; sleep 30 & other=$!; lockwith "$other"; pending "new 41"; runner; kill "$other" 2>/dev/null
check "a lock whose PID is alive but not a run.sh is taken over (M1)" $([ "$rc" = 0 ] && [ "$(lines "$D/claude.log")" = 1 ]; echo $?)

setup; touch "$AGENT_HOME/.agent-stop"; runner
check "an existing stop file exits 0 before any work" $([ "$rc" = 0 ] && no_file "$D/pending.count"; echo $?)

setup; export AGENT_POLL_SECONDS=5
printf '%s' '[{"number":60,"mergedAt":"2027-01-10T10:00:00Z","headRefName":"38-a","closingIssuesReferences":[{"number":38}]},
{"number":61,"mergedAt":"2027-01-12T10:00:00Z","headRefName":"39-b","closingIssuesReferences":[]}]' > "$D/merged.json"
pending IDLE IDLE "merge 50 41" IDLE STOP; runner
check "idle mentions the owner once per empty queue, on the most recently merged slice" \
  $([ "$(lines "$D/comments.log")" = 2 ] && [ "$(cut -d' ' -f2 "$D/comments.log" | sort -u)" = 39 ]; echo $?)
check "idle sleeps the poll interval" $([ "$(sleeps)" = 15 ]; echo $?)

setup; pending IDLE STOP; runner
check "idle without a merged slice only logs" $(no_file "$D/comments.log" && grep -q 'idle' "$AGENT_HOME/agent-logs/runner.log"; echo $?)

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
