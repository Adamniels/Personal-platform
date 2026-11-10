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
# claude: logs its arguments ("|" after each) to claude.log and which gh it would run to claude-gh.log, then plays
# the next line of claude.queue, "<exit> <seconds> <output>" (output through printf %b), or "0 300 done" when the
# queue is empty. <seconds> advances the clock. An exit of "hang" advances the clock and then really sleeps.
cat > "$tmp/bin/claude" <<'FAKE'
#!/usr/bin/env bash
for a in "$@"; do printf '%s|' "$a"; done >> "$D/claude.log"; echo >> "$D/claude.log"
g="$(command -v gh)"; if grep -q 'gh-guard' "$g" 2>/dev/null; then echo guard >> "$D/claude-gh.log"; else echo plain >> "$D/claude-gh.log"; fi
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
  "pr view "*) echo "${FAKE_HEAD:-aaaa}" ;;
  "pr list "*) if [ -f "$D/merged.json" ]; then cat "$D/merged.json"; else echo '[]'; fi ;;
  *) echo "unexpected gh call: $*" >&2; exit 9 ;;
esac
FAKE
cat > "$tmp/bin/docker" <<'FAKE'
#!/usr/bin/env bash
echo "docker $*" >> "$D/probe.log"; exit "${FAKE_DOCKER_EXIT:-0}"
FAKE
cat > "$tmp/bin/df" <<'FAKE'
#!/usr/bin/env bash
echo "Filesystem 1024-blocks Used Available Capacity Mounted"; echo "/dev/x 999999999 1 ${FAKE_DF_KB:-100000000} 1% /"
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
# pending.sh: prints the next line of pending.queue. EXIT1 fails like a GitHub error, IDLE prints nothing, STOP
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
      STOP) : > "$AGENT_HOME/.agent-stop" ;;
      *) echo "$line" ;;
    esac ;;
esac
FAKE
cat > "$tmp/scripts/merge-gate.sh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$D/gate.log"; printf '%b' "${FAKE_GATE_OUT:-merged #$1\n}"; exit "${FAKE_GATE_EXIT:-0}"
FAKE
cat > "$tmp/scripts/board.sh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$D/board.log"; exit "${FAKE_BOARD_EXIT:-0}"
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

pass=0 fail=0
check() { if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi; }
t=0
# setup: a fresh home, data dir, clock and repository (main and branch 41-x, both pushed to a bare origin), and
# every fake knob back to its default.
setup() {
  t=$((t + 1))
  D="$tmp/t$t/data"; AGENT_HOME="$tmp/t$t/home"; AGENT_REPO="$tmp/t$t/repo"
  mkdir -p "$D" "$AGENT_HOME"
  echo "$S" > "$D/clock"
  : > "$D/claude.queue"; : > "$D/pending.queue"
  git init -q --bare "$tmp/t$t/origin.git"
  git init -q -b main "$AGENT_REPO"
  git -C "$AGENT_REPO" config user.email t@example.com; git -C "$AGENT_REPO" config user.name t
  echo base > "$AGENT_REPO/file"; git -C "$AGENT_REPO" add file; git -C "$AGENT_REPO" commit -q -m base
  git -C "$AGENT_REPO" remote add origin "$tmp/t$t/origin.git"
  git -C "$AGENT_REPO" branch 41-x
  git -C "$AGENT_REPO" push -q origin main 41-x 2>/dev/null
  export D AGENT_HOME AGENT_REPO AGENT_POLL_SECONDS=0 AGENT_TIMEOUT_SECONDS=5400
  unset FAKE_STOP_AT FAKE_RATE_EXIT FAKE_COMMENT_EXIT FAKE_LABEL_EXIT FAKE_HEAD FAKE_DOCKER_EXIT FAKE_DF_KB \
        FAKE_GATE_OUT FAKE_GATE_EXIT FAKE_BOARD_EXIT FAKE_VERDICT
}
pending() { printf '%s\n' "$@" > "$D/pending.queue"; }
claude_plays() { printf '%s\n' "$@" > "$D/claude.queue"; }
runner() { bash "$here/run.sh" > "$D/out" 2>&1; rc=$?; }
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

setup; pending "new 41" "new 41"; claude_plays "1 5 rate limit exceeded" "0 300 done"; runner
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

setup; git -C "$AGENT_REPO" remote set-url origin "$tmp/t$t/missing.git"; export FAKE_STOP_AT=$((S + 60)); pending "new 41"; runner
check "a failed fetch is a transient pause with no pending.sh and no claude call" \
  $(no_file "$D/claude.log" && no_file "$D/pending.count" && [ "$(waits transient)" = 60 ]; echo $?)

setup; git clone -q "$tmp/t$t/origin.git" "$tmp/t$t/other" 2>/dev/null
git -C "$tmp/t$t/other" -c user.email=o@x -c user.name=o commit -q --allow-empty -m ahead; git -C "$tmp/t$t/other" push -q origin main 2>/dev/null
git -C "$AGENT_REPO" switch -q 41-x; pending "merge 50 41"; runner
check "after an item a clean tree is switched to main and fast-forwarded to origin/main" \
  $([ "$(git -C "$AGENT_REPO" branch --show-current)" = main ] && [ "$(git -C "$AGENT_REPO" log -1 --format=%s)" = ahead ]; echo $?)

setup; git -C "$AGENT_REPO" switch -q 41-x; echo half > "$AGENT_REPO/file"; pending "merge 50 41"; runner
check "after an item a dirty tree is left where it is" \
  $([ "$(git -C "$AGENT_REPO" branch --show-current)" = 41-x ] && [ "$(cat "$AGENT_REPO/file")" = half ]; echo $?)

setup; export AGENT_POLL_SECONDS=5; pending "cap 50 41" "cap 50 41" "cap 50 41"; runner
check "a repeated cap for the same pr and sha mentions once and waits the poll interval each time (R43b)" \
  $([ "$(lines "$D/comments.log")" = 1 ] && [ "$(tr '\n' ' ' < "$D/sleep.log")" = "5 5 " ]; echo $?)

# --- Lock and idle (R18) ----------------------------------------------------------------------------------------
setup; mkdir -p "$AGENT_HOME/agent-logs"; echo $$ > "$AGENT_HOME/agent-logs/run.lock"; pending "new 41"; runner
check "a live lock exits 2, already running, without asking pending.sh" \
  $([ "$rc" = 2 ] && grep -q 'already running' "$D/out" && no_file "$D/pending.count"; echo $?)

setup; mkdir -p "$AGENT_HOME/agent-logs"; sh -c 'exit 0' & dead=$!; wait "$dead"; echo "$dead" > "$AGENT_HOME/agent-logs/run.lock"
pending "new 41"; runner
check "a stale lock is taken over, and released at exit" \
  $([ "$rc" = 0 ] && [ "$(lines "$D/claude.log")" = 1 ] && no_file "$AGENT_HOME/agent-logs/run.lock"; echo $?)

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
