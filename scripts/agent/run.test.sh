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
if [ -z "${AGENT_GH_GUARD+x}" ]; then echo unset >> "$D/claude-env.log"; elif [ "$AGENT_GH_GUARD" = "$g" ]; then echo first >> "$D/claude-env.log"; else echo other >> "$D/claude-env.log"; fi
if [ -x "$D/claude-hook" ]; then "$D/claude-hook"; fi
line="0 300 done"
if [ -s "$D/claude.queue" ]; then line="$(head -n1 "$D/claude.queue")"; sed -i.bak 1d "$D/claude.queue"; fi
code="${line%% *}"; rest="${line#* }"; secs="${rest%% *}"; out="${rest#* }"
echo $(( $(cat "$D/clock") + secs )) > "$D/clock"
printf '%b\n' "$out"
if [ "$code" = hang ]; then sleep 30 & echo $! > "$D/hang.pid"; wait; exit 0; fi
exit "$code"
FAKE
# gh: logs every call to gh.log; comments go to comments.log as "<clock> <issue> <body>", label changes to labels.log
# as "<pr> +<added> -<removed>". `pr view` prints the head, the state and then the PR's labels, comma separated.
# The reads only the real pending.sh makes are answered too, for the tests that run it: its listing of the open
# pull requests as JSON, each closing slice 41, a head commit's date, and no comments or reviews.
# Labels are kept as GitHub keeps them: a PR starts with FAKE_PR_LABELS, or with what a test wrote to
# $D/labels-<pr>, and every `pr edit` that succeeds changes that file. The state is $D/state-<pr>, else
# FAKE_PR_STATE, else OPEN; the listing below never looks at it, which is what a listing that lags looks like.
# The open pull requests `pr list --state open` prints are the numbers in $D/open-prs, one per line, or PR 50 alone
# without that file, each as "<pr> <head> <labels>". A PR's head is $D/head-<pr>, else $D/head, else FAKE_HEAD, else
# aaaa. review-ok is kept per commit in $D/review-ok-<sha>: reading a commit's status prints it, or "none", and
# posting one stores it and logs "<sha> <state> <description>" to status.log and "status <sha> <state>" to order.log,
# where the fake merge gate logs "gate <pr>". FAKE_STATUS_EXIT=1 makes the post fail, and $D/gh-pre, when a test
# writes it, is sourced before each post.
cat > "$tmp/bin/gh" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$D/gh.log"
head_for() { if [ -f "$D/head-$1" ]; then cat "$D/head-$1"; elif [ -f "$D/head" ]; then cat "$D/head"; else echo "${FAKE_HEAD:-aaaa}"; fi; }
labels_for() { if [ -f "$D/labels-$1" ]; then cat "$D/labels-$1"; else echo "${FAKE_PR_LABELS:-}"; fi; }
state_for() { if [ -f "$D/state-$1" ]; then cat "$D/state-$1"; else echo "${FAKE_PR_STATE:-OPEN}"; fi; }
case "$*" in
  "api rate_limit"*) exit "${FAKE_RATE_EXIT:-0}" ;;
  "api repos/Adamniels/Personal-platform/commits/"*"/status"*)
    sha="${2#repos/Adamniels/Personal-platform/commits/}"; sha="${sha%%/*}"
    if [ -f "$D/review-ok-$sha" ]; then cat "$D/review-ok-$sha"; else echo none; fi ;;
  # The real picker's other reads: a head commit's date, before every verdict, and no comments or reviews.
  "api repos/Adamniels/Personal-platform/commits/"*) echo "2027-01-15T07:00:00Z" ;;
  "api repos/Adamniels/Personal-platform/issues/"*"/comments"*|"api repos/Adamniels/Personal-platform/pulls/"*) echo '[]' ;;
  "api repos/Adamniels/Personal-platform/statuses/"*)
    if [ -f "$D/gh-pre" ]; then . "$D/gh-pre"; fi   # a test's one-off, such as failing the next post only
    [ "${FAKE_STATUS_EXIT:-0}" = 0 ] || { echo "HTTP 502: Bad Gateway" >&2; exit 1; }
    sha="${2#repos/Adamniels/Personal-platform/statuses/}" state="" context="" desc=""
    while [ $# -gt 0 ]; do
      case "$1" in state=*) state="${1#state=}" ;; context=*) context="${1#context=}" ;; description=*) desc="${1#description=}" ;; esac
      shift
    done
    [ "$context" = review-ok ] || { echo "unexpected status context: $context" >&2; exit 9; }
    echo "$state" > "$D/review-ok-$sha"
    echo "$sha $state $desc" >> "$D/status.log"; echo "status $sha $state" >> "$D/order.log" ;;
  "issue comment "*)
    [ "${FAKE_COMMENT_EXIT:-0}" = 0 ] || exit 1
    issue="$3" body=""; while [ $# -gt 0 ]; do [ "$1" = --body ] && body="$2"; shift; done
    echo "$(cat "$D/clock") $issue $(printf '%s' "$body" | tr '\n' ' ')" >> "$D/comments.log" ;;
  "pr edit "*)
    [ "${FAKE_LABEL_EXIT:-0}" = 0 ] || exit 1
    pr="$3" ops="" l="$(labels_for "$3")" cur=","
    [ -z "$l" ] || cur=",$l,"
    while [ $# -gt 0 ]; do
      case "$1" in
        --add-label) ops="$ops +$2"; case "$cur" in *",$2,"*) ;; *) cur="$cur$2," ;; esac ;;
        --remove-label) ops="$ops -$2"; cur="${cur//,$2,/,}" ;;
      esac
      shift
    done
    cur="${cur#,}"; echo "${cur%,}" > "$D/labels-$pr"
    echo "$pr$ops" >> "$D/labels.log" ;;
  "pr view "*) head_for "$3"; state_for "$3"; labels_for "$3" ;;
  "pr list "*" --state open "*"headRefName"*)   # the real picker's listing: the same pull requests, as JSON
    if [ -f "$D/open-prs" ]; then prs="$(cat "$D/open-prs")"; else prs=50; fi
    for n in $prs; do
      jq -n -c --argjson n "$n" --arg h "$(head_for "$n")" --arg l "$(labels_for "$n")" \
        '{number: $n, headRefOid: $h, headRefName: "41-x", labels: [$l | split(",")[] | select(. != "") | {name: .}],
          closingIssuesReferences: [{number: 41}]}'
    done | jq -s -c . ;;
  "pr list "*" --state open "*)
    if [ -f "$D/open-prs" ]; then prs="$(cat "$D/open-prs")"; else prs=50; fi
    for n in $prs; do echo "$n $(head_for "$n") $(labels_for "$n")"; done ;;
  "pr list "*) if [ -f "$D/merged.json" ]; then cat "$D/merged.json"; else echo '[]'; fi ;;
  # A slice's state is $D/issue-<n> (default OPEN); closing it logs "<n> <comment>" to closed.log and makes it
  # CLOSED, unless $D/close-fail-once exists, which fails it once.
  "issue view "*) if [ -f "$D/issue-$3" ]; then cat "$D/issue-$3"; else echo OPEN; fi ;;
  "issue close "*)
    if [ -e "$D/close-fail-once" ]; then rm -f "$D/close-fail-once"; exit 1; fi
    n="$3" body=""; while [ $# -gt 0 ]; do [ "$1" = --comment ] && body="$2"; shift; done
    echo "$n $body" >> "$D/closed.log"; echo CLOSED > "$D/issue-$n" ;;
  *) echo "unexpected gh call: $*" >&2; exit 9 ;;
esac
FAKE
cat > "$tmp/bin/docker" <<'FAKE'
#!/usr/bin/env bash
echo "docker $*" >> "$D/probe.log"; if [ "${FAKE_DOCKER_HANG:-0}" = 1 ]; then sleep 30; fi
[ -f "$D/docker-up" ] && exit 0; exit "${FAKE_DOCKER_EXIT:-0}"
FAKE
# Stand-ins for `open -a Docker`: one brings the fake docker up, the other starts nothing.
cat > "$tmp/bin/fake-docker-start" <<'FAKE'
#!/usr/bin/env bash
echo "start $*" >> "$D/start.log"; : > "$D/docker-up"
FAKE
cat > "$tmp/bin/fake-docker-start-dud" <<'FAKE'
#!/usr/bin/env bash
echo "start $*" >> "$D/start.log"
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
echo "$*" >> "$D/gate.log"; echo "gate $1" >> "$D/order.log"; printf '%b' "${FAKE_GATE_OUT:-merged #$1\n}"; exit "${FAKE_GATE_EXIT:-0}"
FAKE
# board.sh: `get <n>` prints status-<n> (default "In review"); `status <n> <s>` logs to board.log and records <s>.
cat > "$tmp/scripts/board.sh" <<'FAKE'
#!/usr/bin/env bash
if [ "$1" = get ]; then if [ -f "$D/status-$2" ]; then cat "$D/status-$2"; else echo "In review"; fi; exit 0; fi
echo "$*" >> "$D/board.log"; [ "${FAKE_BOARD_EXIT:-0}" = 0 ] || exit "$FAKE_BOARD_EXIT"
if [ -e "$D/board-fail-once" ]; then rm -f "$D/board-fail-once"; exit 1; fi
echo "$3" > "$D/status-$2"
FAKE
# verdict.sh <pr>: the line in $D/verdict-<pr>, else $D/verdict (a claude-hook writes it, as a review would), else
# FAKE_VERDICT, else an approve for aaaa.
cat > "$tmp/scripts/verdict.sh" <<'FAKE'
#!/usr/bin/env bash
if [ -f "$D/verdict-$1" ]; then cat "$D/verdict-$1"; elif [ -f "$D/verdict" ]; then cat "$D/verdict"
else echo "${FAKE_VERDICT:-approve aaaa 0 2027-01-15T08:00:00Z}"; fi
FAKE
# next.sh --blocked: the lines in $D/blocked, "<slice> <blockers>", or nothing.
cat > "$tmp/scripts/next.sh" <<'FAKE'
#!/usr/bin/env bash
[ "$1" = --blocked ] || exit 2
[ ! -f "$D/blocked" ] || cat "$D/blocked"
FAKE
cp "$here/gh-guard.sh" "$tmp/scripts/gh-guard.sh"
cp "$here/pr-slice.sh" "$tmp/scripts/pr-slice.sh"
chmod +x "$tmp/bin/"* "$tmp/scripts/"*
unset AGENT_GH_GUARD
export PATH="$tmp/bin:$PATH" AGENT_SCRIPTS="$tmp/scripts" AGENT_SLEEP="$tmp/bin/fakesleep" TZ=UTC
# Never start Docker unless a test asks for it.
export AGENT_DOCKER_START=""
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
        FAKE_GATE_OUT FAKE_GATE_EXIT FAKE_BOARD_EXIT FAKE_VERDICT FAKE_DOCKER_HANG AGENT_CALL_TIMEOUT_SECONDS \
        AGENT_DOCKER_START_WAIT FAKE_PR_LABELS FAKE_STATUS_EXIT FAKE_PR_STATE
  export AGENT_DOCKER_START=""
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
check "an implementer run blocks Bash commands with the marker text" \
  $(grep -qF '|Bash(*verdict: *)|' "$D/claude.log" && grep -qF '|Bash(*<!--*)|' "$D/claude.log" \
    && ! grep -qF '|Bash(*verdict:*)|' "$D/claude.log"; echo $?)
check "an implementer run blocks PATCH and DELETE through gh api" \
  $(grep -qF '|Bash(*gh api *-X PATCH*)|' "$D/claude.log" && grep -qF '|Bash(*gh api *--method DELETE*)|' "$D/claude.log"; echo $?)
check "an implementer run blocks the merge gate" $(grep -qF '|Bash(*merge-gate.sh*)|' "$D/claude.log"; echo $?)
check "an implementer run sees the gh guard first on PATH (R36)" $([ "$(cat "$D/claude-gh.log")" = guard ]; echo $?)
check "an implementer run gets AGENT_GH_GUARD naming the gh first on its PATH" $([ "$(cat "$D/claude-env.log")" = first ]; echo $?)
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
    && [ "$(cat "$D/claude-gh.log")" = plain ] && [ "$(cat "$D/claude-env.log")" = unset ]; echo $?)

setup; pending "merge 50 41"; runner
check "merge 50 41 calls the merge gate with 50, and no claude" \
  $([ "$(cat "$D/gate.log")" = 50 ] && no_file "$D/claude.log"; echo $?)

setup; export AGENT_POLL_SECONDS=7 FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok is pending\n'; pending "merge 50 41"; runner
check "merge exit 3 with only ci-ok pending sleeps the poll interval, no label, no mention" \
  $([ "$(sleeps)" = 7 ] && no_file "$D/labels.log" && no_file "$D/comments.log" && no_file "$D/board.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='touches protected paths\nrisk is not low\n'; pending "merge 50 41"; runner
check "merge exit 3 with only owner reasons adds owner-merge and says PR #50 is ready to read and merge, once, on the slice" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-merge" ] && [ "$(lines "$D/comments.log")" = 1 ] \
    && grep -q "^$S 41 @Adamniels PR #50 is ready for you to read and merge" "$D/comments.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok failed\nrisk is not low\n'; pending "merge 50 41"; runner
check "merge exit 3 with other reasons adds owner-blocked and mentions the reasons, never as ready" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-blocked" ] && grep -q '@Adamniels PR #50 needs you, do not merge it.*ci-ok failed.*risk is not low' "$D/comments.log" \
    && ! grep -q 'ready' "$D/comments.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='risk is not low\n' FAKE_LABEL_EXIT=1; pending "merge 50 41" "merge 50 41"; runner
check "a failed label is retried without a second mention" \
  $([ "$(lines "$D/comments.log")" = 1 ] && [ "$(grep -c 'add-label' "$D/gh.log")" = 2 ]; echo $?)

setup; export FAKE_GATE_EXIT=1; pending "merge 50 41"; runner
check "merge exit 1 is a transient pause: 60 s, no label, no mention, no count" \
  $([ "$(sleeps)" = 60 ] && no_file "$D/labels.log" && no_file "$D/comments.log" && [ "$(fails 41)" = 0 ]; echo $?)

setup; pending "cap 50 41"; runner
check "cap 50 41 adds owner-blocked and mentions the owner that review rounds ran out" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-blocked" ] && grep -q '^[0-9]* 41 @Adamniels PR #50 .*review rounds' "$D/comments.log"; echo $?)

setup; export FAKE_PR_LABELS=bug,has-decision,in-review FAKE_VERDICT="changes aaaa 4 2027-01-15T08:00:00Z"; pending "cap 50 41" IDLE; runner
check "a cap on a PR with has-decision swaps in-review for owner-blocked and leaves the implementer's label" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-blocked -in-review" ] && [ "$(cat "$D/labels-50")" = "bug,has-decision,owner-blocked" ]; echo $?)

setup; export FAKE_PR_LABELS=owner-merge; pending "cap 50 41"; runner
check "a cap on a PR with owner-merge replaces it with owner-blocked" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-blocked -owner-merge" ]; echo $?)

# --- Owner items: the gate's reasons say ready or stuck, and one mention per PR and head sha (R42) ----------------
READY='risk is not low\nan owner label is set\n'
setup; export AGENT_POLL_SECONDS=5 FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$READY" FAKE_PR_LABELS=has-decision,owner-decision; pending "owner 50 41" "owner 50 41"; runner
check "an owner item for a PR with has-decision and owner-decision mentions once that it waits for a decision, records pr and sha, and changes no label" \
  $([ "$(lines "$D/comments.log")" = 1 ] && grep -q ' 41 @Adamniels PR #50 is approved and waits for your decision: ' "$D/comments.log" \
    && [ "$(cat "$AGENT_HOME/agent-logs/owner-told")" = "50 aaaa" ] && [ "$(cat "$D/gate.log" | sort -u)" = 50 ] && no_file "$D/labels.log"; echo $?)
check "the same pr and sha again comments nothing and waits the poll interval" $([ "$(sleeps)" = 5 ]; echo $?)
rm -f "$AGENT_HOME/.agent-stop"; export FAKE_HEAD=bbbb FAKE_VERDICT="approve bbbb 0 2027-01-15T08:00:00Z"; pending "owner 50 41"; runner
check "a new head sha gets one new mention" \
  $([ "$(lines "$D/comments.log")" = 2 ] && grep -qx '50 bbbb' "$AGENT_HOME/agent-logs/owner-told"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$READY" FAKE_PR_LABELS=needs-owner; pending "owner 50 41"; runner
check "needs-owner, the older label, is no longer read: the pull request is ready to read and merge like any other" \
  $(grep -q 'PR #50 is ready for you to read and merge' "$D/comments.log" && [ "$(cat "$D/labels.log")" = "50 +owner-merge" ]; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$READY" FAKE_PR_LABELS=owner-merge; pending "owner 50 41"; runner
check "an owner item for a PR that already carries owner-merge says ready to read and merge and edits no label" \
  $(grep -q 'PR #50 is ready for you to read and merge' "$D/comments.log" && no_file "$D/labels.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$READY" FAKE_PR_LABELS=owner-blocked; pending "owner 50 41"; runner
check "an owner-blocked PR that is approved and ready swaps to owner-merge" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-merge -owner-blocked" ] && grep -q 'ready for you to read and merge' "$D/comments.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$READY" FAKE_PR_LABELS=has-decision,owner-blocked; pending "owner 50 41"; runner
check "an owner-blocked PR with has-decision that is ready swaps to owner-decision and gets no owner-merge" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-decision -owner-blocked" ] && grep -q 'waits for your decision' "$D/comments.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok failed\nan owner label is set\n' FAKE_PR_LABELS=owner-merge; pending "owner 50 41"; runner
check "an owner item the gate refuses for another reason is stuck: owner-blocked replaces owner-merge, never 'ready'" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-blocked -owner-merge" ] && grep -q 'PR #50 needs you, do not merge it' "$D/comments.log" \
    && ! grep -q 'ready' "$D/comments.log"; echo $?)

setup; export AGENT_POLL_SECONDS=7 FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok is pending\nan owner label is set\n' FAKE_PR_LABELS=has-decision,owner-decision; pending "owner 50 41"; runner
check "an owner item whose ci-ok is pending waits: no mention yet, no label" \
  $([ "$(sleeps)" = 7 ] && no_file "$D/comments.log" && no_file "$D/labels.log"; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='risk is not low\n'; pending "merge 50 41" "owner 50 41"; runner
check "a merge-gate ready mention, then an owner item for the same sha, mentions once" $([ "$(lines "$D/comments.log")" = 1 ]; echo $?)

setup; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$READY"; pending "cap 50 41" "owner 50 41"; runner
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
check "two slow reviews that leave no verdict set Blocked and add owner-blocked to the PR" \
  $(grep -q 'status 41 Blocked' "$D/board.log" && grep -q '50 +owner-blocked' "$D/labels.log"; echo $?)

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

# Docker Desktop quits to update and may not come back: the runner starts it once and waits for it.
setup; export FAKE_DOCKER_EXIT=1 AGENT_DOCKER_START="$tmp/bin/fake-docker-start"; pending "new 41" "STOP"; runner
check "a down Docker is started once, answers, and the run goes ahead with no pause" \
  $([ "$(lines "$D/start.log")" = 1 ] && [ -s "$D/claude.log" ] && [ "$(sleeps)" = 10 ] \
    && grep -q 'docker is back after 10 s' "$AGENT_HOME/agent-logs/runner.log"; echo $?)
setup; export FAKE_DOCKER_EXIT=1 AGENT_DOCKER_START="$tmp/bin/fake-docker-start-dud" AGENT_DOCKER_START_WAIT=30
pending "new 41" "STOP"; runner
check "a Docker that does not answer after its start is an environment pause with no claude call" \
  $([ "$(lines "$D/start.log")" = 1 ] && no_file "$D/claude.log" && no_file "$D/board.log" \
    && grep -q 'docker did not answer within 30 s' "$AGENT_HOME/agent-logs/runner.log" \
    && grep -q 'pausing (environment: docker info failed)' "$AGENT_HOME/agent-logs/runner.log"; echo $?)
setup; export FAKE_DOCKER_EXIT=1; pending "new 41" "STOP"; runner
check "with AGENT_DOCKER_START empty the runner never starts Docker" \
  $(no_file "$D/start.log" && no_file "$D/claude.log"; echo $?)

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
# The review is of another pull request: the runner is done with 50 once the gate has merged it. It ends in an
# approve, so the gate is called for 51 in the review's own turn, from main like every other call.
pending "merge 50 41" "review 51 41" "new 41"; runner
check "pending, the gate, verdict and the guard come from origin/main, never the checked-out branch" \
  $(no_file "$D/trap.log" && [ "$(tr '\n' ' ' < "$D/gate.log")" = "50 51 " ] && [ "$(lines "$D/claude.log")" = 2 ] \
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

setup; export FAKE_DOCKER_HANG=1 AGENT_CALL_TIMEOUT_SECONDS=3 FAKE_STOP_AT=$((S + 60)); pending "REPEAT new 41"
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

# R55: a half-done Block (mention made, board write failed) is finished before any other item of the slice.
setup; : > "$D/board-fail-once"; pending "fix 50 41" "fix 50 41" "merge 50 41"; claude_plays "1 300 tests fail" "1 300 tests fail"; runner
check "R55: after a failed Blocked write the next item (merge) does not merge; the Block completes, mentioned once" \
  $(no_file "$D/gate.log" && [ "$(lines "$D/comments.log")" = 1 ] && [ "$(cat "$D/status-41")" = Blocked ] \
    && grep -q '^50 ' "$AGENT_HOME/agent-logs/blocked-prs" && grep -q '50 +owner-blocked' "$D/labels.log"; echo $?)
check "R55: the Blocked notice says how to run it again" \
  $(grep -q 'Comment on this PR or move the slice off Blocked to run it again\.$' "$D/comments.log"; echo $?)

setup; export FAKE_VERDICT="none 0 0 -"; pending "review 50 41" "review 50 41"; runner
again; echo "In review" > "$D/status-41"; : > "$AGENT_HOME/agent-logs/mentioned/blocked-41"; echo 1 > "$AGENT_HOME/agent-logs/failures/41"
pending "review 50 41" "review 50 41"; runner
check "R55: an owner unblock clears the slice's marker and count, so a later Block runs twice and mentions again" \
  $([ "$(lines "$D/claude.log")" = 4 ] && [ "$(lines "$D/comments.log")" = 2 ] && [ "$(grep -c 'status 41 Blocked' "$D/board.log")" = 2 ]; echo $?)

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

setup; export AGENT_CALL_TIMEOUT_SECONDS=3 FAKE_STOP_AT=$((S + 60)); hook pending-hook ": > '$D/git-hang'"; pending HOOK "new 41"; runner
check "a hanging fetch times out into a transient pause, logged with the git verb" \
  $(grep -q 'git fetch timed out after 3 s' "$AGENT_HOME/agent-logs/runner.log" && [ "$(waits transient)" = 60 ] && no_file "$D/claude.log"; echo $?)

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

setup
printf '%s' '[{"number":60,"mergedAt":"2027-01-10T10:00:00Z","headRefName":"38-a","closingIssuesReferences":[{"number":38}]}]' > "$D/merged.json"
printf '443 #442\n444 #443,#440\n' > "$D/blocked"; pending IDLE STOP; runner
check "idle with Ready slices all blocked names the first one and what it waits for" \
  $(grep -q "38 @Adamniels the runner's queue is empty: no pull request waits on it and no Ready slice can start: #443 waits for #442, and 1 more Ready slices wait for an open blocker. It looks again" "$D/comments.log"; echo $?)

setup
printf '%s' '[{"number":60,"mergedAt":"2027-01-10T10:00:00Z","headRefName":"38-a","closingIssuesReferences":[{"number":38}]}]' > "$D/merged.json"
pending IDLE STOP; runner
check "idle with nothing blocked says no slice is Ready" \
  $(grep -q "no pull request waits on it and no slice is Ready. It looks again" "$D/comments.log"; echo $?)

# --- A merged slice is closed (#472) ----------------------------------------------------------------------------
# GitHub did not link #471 to #442, so the owner's merge closed nothing and #443 onwards waited on it for ever.
ref() { printf '{"number":%s,"repository":{"name":"Personal-platform","owner":{"login":"Adamniels"}}}' "$1"; }
setup
printf '%s' "[{\"number\":70,\"headRefName\":\"44-a\",\"body\":\"Closes #44\",\"closingIssuesReferences\":[]},
{\"number\":71,\"headRefName\":\"45-b\",\"body\":\"Closes #45\",\"closingIssuesReferences\":[$(ref 45)]},
{\"number\":72,\"headRefName\":\"46-c\",\"body\":\"Closes #46\",\"closingIssuesReferences\":[]},
{\"number\":73,\"headRefName\":\"47-d\",\"body\":\"Closes #48\",\"closingIssuesReferences\":[]}]" > "$D/merged.json"
echo CLOSED > "$D/issue-46"
pending IDLE IDLE STOP; runner
check "a merged slice GitHub did not link and left open is closed, with why, once" \
  $([ "$(lines "$D/closed.log")" = 1 ] && grep -qx "44 Closed by the runner: its pull request #70 is merged, and GitHub did not link the two." "$D/closed.log"; echo $?)
check "the slice the runner closes is moved to Done" $(grep -qx "status 44 Done" "$D/board.log"; echo $?)
check "a slice GitHub linked is left to GitHub, unread" $(! grep -q "issue view 45" "$D/gh.log"; echo $?)
check "a slice already closed is left alone, and read once" \
  $([ "$(grep -c "issue view 46" "$D/gh.log")" = 1 ] && ! grep -q '^46 ' "$D/closed.log"; echo $?)
check "a pull request whose body closes another issue than its branch's closes nothing" \
  $(! grep -q "issue view 4[78]" "$D/gh.log"; echo $?)
check "every merged pull request is settled once" \
  $([ "$(sort -n "$AGENT_HOME/agent-logs/settled-prs" | tr '\n' ' ')" = "70 71 72 73 " ]; echo $?)

setup
printf '%s' '[{"number":70,"headRefName":"44-a","body":"Closes #44","closingIssuesReferences":[]}]' > "$D/merged.json"
: > "$D/close-fail-once"; pending IDLE IDLE STOP; runner
check "a failed close is tried again the next loop, without a pause" \
  $([ "$(lines "$D/closed.log")" = 1 ] && no_file "$AGENT_HOME/agent-logs/pause" && grep -q "could not close #44" "$AGENT_HOME/agent-logs/runner.log"; echo $?)

setup
printf '%s' '[{"number":70,"headRefName":"44-a","body":"Closes #44","closingIssuesReferences":[]}]' > "$D/merged.json"
export FAKE_BOARD_EXIT=1; pending IDLE IDLE STOP; runner; unset FAKE_BOARD_EXIT
# Project 5's "Item closed → Done" workflow moves a closed slice too, so a failed move only waits for that.
check "a failed move to Done is logged, the slice is not closed twice, and it is settled the next loop" \
  $([ "$(lines "$D/closed.log")" = 1 ] && grep -q "could not move it to Done" "$AGENT_HOME/agent-logs/runner.log" \
     && grep -qx 70 "$AGENT_HOME/agent-logs/settled-prs"; echo $?)

# --- review-ok, the commit status the ruleset requires ----------------------------------------------------------
APPROVE="approve aaaa 0 2027-01-15T08:00:00Z"
setup; export FAKE_VERDICT="none 0 0 -"; hook claude-hook "echo '$APPROVE' > '$D/verdict'"; pending "review 50 41"; runner
check "an approve for the head sets review-ok to success on that commit" \
  $([ "$(tail -n1 "$D/status.log")" = "aaaa success approved by the reviewer" ] && [ "$(cat "$D/review-ok-aaaa")" = success ] \
    && grep -q '^api repos/Adamniels/Personal-platform/statuses/aaaa .*state=success' "$D/gh.log" && [ "$(fails 41)" = 0 ]; echo $?)

setup; export FAKE_VERDICT="none 0 0 -"
hook claude-hook "cp '$D/status.log' '$D/status-at-claude'; echo 'changes aaaa 1 2027-01-15T08:00:00Z' > '$D/verdict'"
pending "review 50 41"; runner
check "a head with no approve gets review-ok pending" \
  $([ "$(cat "$D/status-at-claude")" = "aaaa pending waiting for the reviewer" ] && [ "$(cat "$D/status.log")" = "aaaa pending waiting for the reviewer" ] \
    && [ "$(cat "$D/review-ok-aaaa")" = pending ] && ! grep -q 'state=failure' "$D/gh.log"; echo $?)

setup; hook claude-hook "echo bbbb > '$D/head'"; pending "fix 50 41" IDLE; runner
check "a new commit on an approved pull request has review-ok pending, and the old commit keeps its success" \
  $([ "$(cat "$D/review-ok-aaaa")" = success ] && [ "$(cat "$D/review-ok-bbbb")" = pending ]; echo $?)

setup; pending "merge 50 41"; runner
check "review-ok is set before the gate is called" \
  $([ "$(tr '\n' ' ' < "$D/order.log")" = "status aaaa success gate 50 " ]; echo $?)

# The stop comes from the clock, since a runner that cannot write the status never reaches pending.sh.
setup; export FAKE_STATUS_EXIT=1 FAKE_STOP_AT=$((S + 150)); pending "merge 50 41"; runner
check "a failed status write is a transient pause, never a merge" \
  $(no_file "$D/gate.log" && [ "$(waits transient)" = "60 120" ] && no_file "$D/pending.count" && no_file "$D/labels.log" \
    && no_file "$D/comments.log" && [ "$(fails 41)" = 0 ] && no_file "$D/board.log"; echo $?)

setup; export FAKE_VERDICT="none 0 0 -"; hook claude-hook "echo '$APPROVE' > '$D/verdict'; touch '$D/status-fails'"
pending "review 50 41" "merge 50 41"; echo 'if [ -e "$D/status-fails" ]; then rm -f "$D/status-fails"; export FAKE_STATUS_EXIT=1; fi' > "$D/gh-pre"; runner
check "a status write that fails after a review is a transient pause, and the next loop writes it before the gate" \
  $([ "$(waits transient)" = 60 ] && [ "$(fails 41)" = 0 ] && [ "$(tr '\n' ' ' < "$D/order.log")" = "status aaaa pending status aaaa success gate 50 " ]; echo $?)

setup; export FAKE_HEAD=bbbb; pending "merge 50 41"; runner
check "a merge item whose head has no approve in this loop does not reach the gate and tells nobody" \
  $(no_file "$D/gate.log" && [ "$(cat "$D/labels.log")" = "50 +in-review" ] && no_file "$D/comments.log" \
    && [ "$(cat "$D/review-ok-bbbb")" = pending ] && [ "$(fails 41)" = 0 ] && [ -z "$(waits transient)" ]; echo $?)

setup; printf '50\n51\n' > "$D/open-prs"; echo cccc > "$D/head-51"; echo "none 0 0 -" > "$D/verdict-51"; pending IDLE IDLE IDLE; runner
check "review-ok is made true for every open pull request each loop, and one already right costs no write" \
  $([ "$(cat "$D/status.log")" = "aaaa success approved by the reviewer
cccc pending waiting for the reviewer" ] && [ "$(grep -c 'pr list .*--state open' "$D/gh.log")" -ge 3 ]; echo $?)

# --- The labels are the runner's: in-review in the loop, an owner label when it is the owner's turn -------------
NONE="none 0 0 -"
setup; export FAKE_VERDICT="$NONE"; pending IDLE "review 50 41" IDLE; hook claude-hook "echo 'changes aaaa 1 2027-01-15T08:00:00Z' > '$D/verdict'"; runner
check "a pull request in the loop carries in-review and no owner label" \
  $([ "$(cat "$D/labels.log")" = "50 +in-review" ] && [ "$(cat "$D/labels-50")" = in-review ]; echo $?)

for reason in 'risk is not low' 'touches protected paths'; do
  setup; export FAKE_PR_LABELS=in-review FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$reason\n"; pending "merge 50 41" IDLE; runner
  check "an approved pull request that waits for the owner swaps in-review for owner-merge ($reason)" \
    $([ "$(cat "$D/labels.log")" = "50 +owner-merge -in-review" ] && [ "$(cat "$D/labels-50")" = owner-merge ] \
      && grep -q 'PR #50 is ready for you to read and merge' "$D/comments.log"; echo $?)
done

setup; export FAKE_PR_LABELS=in-review,has-decision FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$READY"; pending "owner 50 41" IDLE; runner
check "an approved pull request with has-decision gets owner-decision, never owner-merge" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-decision -in-review" ] && [ "$(cat "$D/labels-50")" = "has-decision,owner-decision" ] \
    && [ "$(lines "$D/comments.log")" = 1 ] && grep -q ' 41 @Adamniels PR #50 is approved and waits for your decision' "$D/comments.log" \
    && ! grep -q 'owner-merge' "$D/gh.log"; echo $?)

for label in owner-merge owner-decision owner-blocked; do
  setup; export FAKE_PR_LABELS="has-decision,$label" FAKE_HEAD=bbbb; pending IDLE IDLE; runner
  check "a new commit takes the owner label off and puts in-review back ($label)" \
    $([ "$(cat "$D/labels.log")" = "50 +in-review -$label" ] && [ "$(cat "$D/labels-50")" = "has-decision,in-review" ] \
      && [ "$(cat "$D/review-ok-bbbb")" = pending ]; echo $?)
done

setup; export FAKE_PR_LABELS=owner-decision FAKE_VERDICT="$NONE"; pending IDLE IDLE; runner
check "an owner-decision label with no approve for the head is turned into has-decision and in-review" \
  $([ "$(cat "$D/labels.log")" = "50 +has-decision +in-review -owner-decision" ] && [ "$(cat "$D/labels-50")" = "has-decision,in-review" ] \
    && [ "$(cat "$D/review-ok-aaaa")" = pending ] && no_file "$D/comments.log"; echo $?)

# Three pull requests as the change finds them. 50 was approved before it landed and waits for the owner; 51 is in
# review; 52 was opened under the old procedure, carries owner-decision, and is approved.
setup; printf '50\n51\n52\n' > "$D/open-prs"; echo cccc > "$D/head-51"; echo dddd > "$D/head-52"
echo "$NONE" > "$D/verdict-51"; echo "approve dddd 0 2027-01-15T08:00:00Z" > "$D/verdict-52"
: > "$D/labels-50"; : > "$D/labels-51"; echo owner-decision > "$D/labels-52"
export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='risk is not low\n'; pending "merge 50 41" IDLE IDLE; runner
check "the labels and the status are made true for every open pull request each loop" \
  $([ "$(cat "$D/status.log")" = "aaaa success approved by the reviewer
cccc pending waiting for the reviewer
dddd success approved by the reviewer" ] && [ "$(cat "$D/labels.log")" = "51 +in-review
52 +has-decision
50 +owner-merge" ] && [ "$(cat "$D/labels-52")" = "owner-decision,has-decision" ] && no_file "$D/claude.log"; echo $?)

setup; export FAKE_PR_LABELS=in-review FAKE_VERDICT="$NONE"; echo pending > "$D/review-ok-aaaa"; pending IDLE IDLE IDLE; runner
check "a pull request that is already right costs no GitHub write, and no read beyond the listing and its status" \
  $(no_file "$D/labels.log" && no_file "$D/status.log" && ! grep -Eq '^(pr edit|pr view|issue comment)|statuses/' "$D/gh.log" \
    && [ "$(grep -c 'pr list .*--state open' "$D/gh.log")" -ge 3 ]; echo $?)

setup; export FAKE_PR_LABELS=owner-blocked FAKE_VERDICT="changes aaaa 4 2027-01-15T08:00:00Z"; pending IDLE IDLE; runner
check "owner-blocked stays on a pull request whose review rounds ran out at its head, with no in-review beside it" \
  $(no_file "$D/labels.log"; echo $?)

setup; export FAKE_PR_LABELS=owner-blocked FAKE_VERDICT="$NONE"; mkdir -p "$AGENT_HOME/agent-logs"; echo "50 aaaa" > "$AGENT_HOME/agent-logs/blocked-prs"
echo Blocked > "$D/status-41"; pending IDLE IDLE; runner
check "owner-blocked stays on the pull request of a slice the runner set Blocked" $(no_file "$D/labels.log"; echo $?)

setup; export FAKE_PR_LABELS=owner-merge FAKE_LABEL_EXIT=1 FAKE_HEAD=bbbb FAKE_STOP_AT=$((S + 60)); pending "new 41"; runner
check "a failed label write is a transient pause before the picker is asked" \
  $([ "$(waits transient)" = 60 ] && no_file "$D/pending.count" && no_file "$D/claude.log" && no_file "$D/comments.log"; echo $?)

# --- After a merge the runner is done with the pull request ------------------------------------------------------
quiet() { no_file "$D/labels.log" && no_file "$D/comments.log" && no_file "$D/board.log" && [ "$(fails 41)" = 0 ] \
  && [ -z "$(waits transient)$(waits environment)" ] && no_file "$AGENT_HOME/agent-logs/pause"; }
setup; pending "merge 50 41" "merge 50 41" "owner 50 41" "review 50 41"; runner
check "a pull request the gate merged gets no label and no mention afterwards" \
  $([ "$(cat "$D/gate.log")" = 50 ] && quiet && no_file "$D/claude.log" \
    && [ "$(grep -c '#50 is merged; nothing to do' "$AGENT_HOME/agent-logs/runner.log")" = 3 ]; echo $?)

# A runner that did not see the merge itself (it was restarted, or the owner merged): the gate says so.
setup; export FAKE_PR_LABELS=in-review FAKE_GATE_EXIT=3 FAKE_GATE_OUT='pull request is already merged\n'; pending "merge 50 41" "merge 50 41"; runner
check "the gate's already merged answer is nothing to do: no label, no mention, no pause, and no second gate call" \
  $([ "$(cat "$D/gate.log")" = 50 ] && quiet && grep -q '#50 is already merged' "$AGENT_HOME/agent-logs/runner.log"; echo $?)

setup; export FAKE_PR_STATE=MERGED FAKE_VERDICT="$NONE" FAKE_PR_LABELS=owner-merge; pending "merge 50 41" "cap 50 41" "fix 50 41"; runner
check "a merged pull request a lagging listing still shows gets no label, no status and no run" \
  $(quiet && no_file "$D/status.log" && no_file "$D/gate.log" && no_file "$D/claude.log"; echo $?)

setup; export FAKE_PR_LABELS=in-review; pending "merge 50 41"; runner
check "in-review comes off when the gate merges" $([ "$(cat "$D/labels.log")" = "50 -in-review" ] && no_file "$D/comments.log"; echo $?)

# #206: the owner merged while the reviewer was reading, and the reviewer, rightly, posted nothing.
setup; export FAKE_VERDICT="$NONE" FAKE_PR_LABELS=in-review; echo pending > "$D/review-ok-aaaa"
hook claude-hook "echo MERGED > '$D/state-50'"; pending "review 50 41" "review 50 41"; runner
merged_mid=$(quiet && [ "$(lines "$D/claude.log")" = 1 ] && no_file "$D/status.log" \
  && grep -q 'the review of #50 found it merged' "$AGENT_HOME/agent-logs/runner.log"; echo $?)
setup; export FAKE_VERDICT="$NONE" FAKE_PR_STATE=CLOSED; pending "review 50 41" "review 50 41"; runner
check "a review of a merged or closed pull request is not a failure" \
  $([ "$merged_mid" = 0 ] && quiet && no_file "$D/claude.log" && no_file "$D/status.log" \
    && [ "$(grep -c '#50 is closed; nothing to do' "$AGENT_HOME/agent-logs/runner.log")" = 2 ]; echo $?)

setup; export AGENT_POLL_SECONDS=600; pending "merge 50 41" "merge 50 41" "new 41"; runner
check "a dropped item waits a minute at most before the picker is asked again, and other work goes on" \
  $([ "$(sleeps)" = 60 ] && [ "$(lines "$D/claude.log")" = 1 ]; echo $?)

# --- The picker and the runner together: a handover comes before new work and does not hold it up ---------------
# The real pending.sh picks here, over the fake gh's pull request 50 (slice 41) and a fake board: next.sh offers
# the slice in $D/ready as the next Ready one. The claude-hook notes what the pull request carried and how long the
# runner had slept when the implementer started, and takes the slice off Ready, as an implementer run would. The
# wrapper stops the loop at call FAKE_LOOPS, since the real picker never does, and at its nth call it copies what
# pull request 50 carried, the mentions made, the gate's calls and the sleeps so far to <file>-at-pick-<n>, so a
# test can say what was true before the picker was asked again. The gate here is the fake one, except that one
# that merges also takes the pull request, the only one these tests have, off the open listing, as GitHub does.
mkdir -p "$tmp/scripts-picker"; cp "$tmp/scripts/"* "$tmp/scripts-picker/"
cat > "$tmp/scripts-picker/pending.sh" <<'FAKE'
#!/usr/bin/env bash
n=$(( $(cat "$D/pending.count" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$D/pending.count"
for f in labels-50 comments.log gate.log sleep.log; do cp "$D/$f" "$D/${f%.log}-at-pick-$n" 2>/dev/null; done
[ "$n" -ge "${FAKE_LOOPS:-30}" ] && { : > "$AGENT_HOME/.agent-stop"; exit 0; }
exec bash "$REAL_PENDING"
FAKE
cat > "$tmp/scripts-picker/merge-gate.sh" <<'FAKE'
#!/usr/bin/env bash
bash "$BASE_GATE" "$@"; rc=$?
if [ "$rc" = 0 ]; then echo MERGED > "$D/state-$1"; : > "$D/open-prs"; fi
exit "$rc"
FAKE
cat > "$tmp/scripts-picker/next.sh" <<'FAKE'
#!/usr/bin/env bash
[ "${1:-}" = --machine ] && [ $# -eq 1 ] || { echo "unexpected next.sh call: $*" >&2; exit 9; }
if [ -f "$D/ready" ]; then echo "next $(cat "$D/ready")"; fi
FAKE
chmod +x "$tmp/scripts-picker/"*
export REAL_PENDING="$here/pending.sh" BASE_GATE="$tmp/scripts/merge-gate.sh"
at_claude() { hook claude-hook "cp '$D/labels-50' '$D/labels-at-claude' 2>/dev/null; cp '$D/comments.log' '$D/comments-at-claude' 2>/dev/null; cp '$D/sleep.log' '$D/sleep-at-claude' 2>/dev/null; rm -f '$D/ready'"; }

setup; export AGENT_SCRIPTS="$tmp/scripts-picker" FAKE_LOOPS=3 AGENT_POLL_SECONDS=600 FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$READY" FAKE_PR_LABELS=in-review,has-decision
echo 42 > "$D/ready"; at_claude; runner
check "a handed over pull request does not hold up the next slice" \
  $([ "$(grep -c 'next: ' "$AGENT_HOME/agent-logs/runner.log")" = 2 ] \
    && [ "$(grep 'next: ' "$AGENT_HOME/agent-logs/runner.log" | sed 's/.*next: //' | tr '\n' ',')" = "owner 50 41,new 42," ] \
    && [ "$(cat "$D/labels-at-claude" 2>/dev/null)" = "has-decision,owner-decision" ] \
    && [ "$(lines "$D/comments-at-claude")" = 1 ] && grep -q ' 41 @Adamniels PR #50 is approved and waits for your decision' "$D/comments-at-claude" \
    && grep -q '^-p|/pick-up-slice 42|' "$D/claude.log" && [ "$(cat "$D/gate.log")" = 50 ] \
    && no_file "$D/sleep-at-claude" && no_file "$D/sleep.log" && [ "$(lines "$D/comments.log")" = 1 ]; echo $?)

# ci-ok still pending on the approved head: the gate cannot say yet whether the pull request is ready or stuck, so
# it gets no owner label and no mention, and its item comes back every poll interval until ci-ok has finished.
setup; export AGENT_SCRIPTS="$tmp/scripts-picker" FAKE_LOOPS=3 AGENT_POLL_SECONDS=7 FAKE_GATE_EXIT=3 FAKE_PR_LABELS=in-review,has-decision
export FAKE_GATE_OUT='ci-ok is pending\nan owner label is set\n'; echo 42 > "$D/ready"; at_claude; runner
check "a pull request with has-decision and ci-ok still pending is looked at again and does not take an owner label" \
  $([ "$(tr '\n' ' ' < "$D/gate.log")" = "50 50 " ] && no_file "$D/labels.log" && no_file "$D/labels-50" && no_file "$D/comments.log" \
    && [ "$(tr '\n' ' ' < "$D/sleep.log")" = "7 7 " ] && no_file "$D/claude.log"; echo $?)
again; export FAKE_GATE_OUT="$READY"; runner
check "once ci-ok has finished it is handed over, and the next slice starts with no further wait" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-decision -in-review" ] && [ "$(cat "$D/labels-at-claude" 2>/dev/null)" = "has-decision,owner-decision" ] \
    && [ "$(lines "$D/comments.log")" = 1 ] && grep -q '^-p|/pick-up-slice 42|' "$D/claude.log" \
    && [ "$(tr '\n' ' ' < "$D/sleep.log")" = "7 7 " ]; echo $?)
export AGENT_SCRIPTS="$tmp/scripts"; unset FAKE_LOOPS

# --- An approve is settled in the same turn: review, then gate, then merged or handed over -----------------------
# The review here leaves its verdict as a reviewer does, during the run: the claude-hook writes it, and anything
# else a test wants to have happened meanwhile. Most of these run the real picker, so "before the picker is asked
# again" is read from the copies its wrapper made at its second call.
approves() { hook claude-hook "echo '$APPROVE' > '$D/verdict'${1:+; $1}"; }
nexts() { grep 'next: ' "$AGENT_HOME/agent-logs/runner.log" | sed 's/.*next: //' | tr '\n' ','; }
same_turn() { export AGENT_SCRIPTS="$tmp/scripts-picker" FAKE_VERDICT="$NONE" FAKE_LOOPS="$1"; }

setup; same_turn 4; export AGENT_POLL_SECONDS=5 FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$READY" FAKE_PR_LABELS=in-review,has-decision
approves; runner
check "an approve hands the pull request to the owner in the same turn" \
  $([ "$(cat "$D/labels-50-at-pick-2" 2>/dev/null)" = "has-decision,owner-decision" ] && [ "$(lines "$D/comments-at-pick-2")" = 1 ] \
    && grep -q ' 41 @Adamniels PR #50 is approved and waits for your decision' "$D/comments-at-pick-2" \
    && [ "$(cat "$D/gate-at-pick-2")" = 50 ] && no_file "$D/sleep-at-pick-2" \
    && [ "$(cat "$D/labels.log")" = "50 +owner-decision -in-review" ]; echo $?)
check "a pull request settled in the same turn is not settled again" \
  $([ "$(nexts)" = "review 50 41," ] && [ "$(cat "$D/pending.count")" = 4 ] && [ "$(lines "$D/comments.log")" = 1 ] \
    && [ "$(cat "$D/gate.log")" = 50 ] && [ "$(cat "$D/labels.log")" = "50 +owner-decision -in-review" ] \
    && [ "$(tr '\n' ' ' < "$D/sleep.log")" = "5 5 " ] && [ "$(cat "$AGENT_HOME/agent-logs/owner-told")" = "50 aaaa" ]; echo $?)

turn_merge=0
for reason in 'risk is not low' 'touches protected paths'; do
  setup; same_turn 2; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$reason\n" FAKE_PR_LABELS=in-review; approves; runner
  [ "$(cat "$D/labels-50-at-pick-2" 2>/dev/null)" = owner-merge ] && [ "$(lines "$D/comments-at-pick-2")" = 1 ] \
    && grep -q " 41 @Adamniels PR #50 is ready for you to read and merge: .*($reason)" "$D/comments-at-pick-2" \
    && [ "$(cat "$D/gate-at-pick-2")" = 50 ] && [ "$(cat "$D/labels.log")" = "50 +owner-merge -in-review" ] || turn_merge=1
done
check "an approve on a pull request the gate leaves to the owner gets owner-merge in the same turn" "$turn_merge"

setup; same_turn 3; export AGENT_POLL_SECONDS=5 FAKE_PR_LABELS=in-review; approves; runner
check "an approve on a pull request the gate can merge is merged in the same turn" \
  $([ "$(cat "$D/gate.log")" = 50 ] && [ "$(cat "$D/gate-at-pick-2")" = 50 ] && [ "$(nexts)" = "review 50 41," ] \
    && [ "$(cat "$D/labels.log")" = "50 -in-review" ] && no_file "$D/comments.log" && no_file "$D/sleep-at-pick-2" \
    && [ "$(cat "$AGENT_HOME/agent-logs/merged-prs")" = 50 ] && [ "$(fails 41)" = 0 ]; echo $?)

# First the review's own turn, stopped at the picker's second call. Then the loop goes on with ci-ok still pending:
# the owner item waits the poll interval, as it does today. Then ci-ok has reported, and the item hands over.
setup; same_turn 2; export AGENT_POLL_SECONDS=7 FAKE_GATE_EXIT=3 FAKE_PR_LABELS=in-review,has-decision
export FAKE_GATE_OUT='ci-ok is pending\nan owner label is set\n'; approves; runner
turn_ci=$([ "$(cat "$D/gate-at-pick-2")" = 50 ] && no_file "$D/labels.log" && no_file "$D/comments.log" && no_file "$D/sleep.log" \
  && no_file "$AGENT_HOME/agent-logs/ci-pending" && no_file "$AGENT_HOME/agent-logs/owner-told" && [ "$(fails 41)" = 0 ] \
  && [ -z "$(waits transient)" ] && [ "$(cat "$D/review-ok-aaaa")" = success ]; echo $?)
again; same_turn 3; runner
turn_ci_item=$([ "$(nexts)" = "review 50 41,owner 50 41,owner 50 41," ] && [ "$(tr '\n' ' ' < "$D/sleep.log")" = "7 7 " ] \
  && no_file "$D/labels.log" && no_file "$D/comments.log" && [ "$(lines "$D/claude.log")" = 1 ]; echo $?)
again; same_turn 3; export FAKE_GATE_OUT="$READY"; runner
check "an approve while ci-ok is pending goes back to the picker without waiting" \
  $([ "$turn_ci" = 0 ] && [ "$turn_ci_item" = 0 ] && [ "$(cat "$D/labels.log")" = "50 +owner-decision -in-review" ] \
    && [ "$(lines "$D/comments.log")" = 1 ] && grep -q 'PR #50 is approved and waits for your decision' "$D/comments.log" \
    && [ "$(lines "$D/claude.log")" = 1 ]; echo $?)

setup; same_turn 2; export FAKE_GATE_EXIT=3 FAKE_GATE_OUT='ci-ok failed\nrisk is not low\n' FAKE_PR_LABELS=in-review; approves; runner
check "an approve the gate refuses for another reason is blocked in the same turn" \
  $([ "$(cat "$D/labels-50-at-pick-2" 2>/dev/null)" = owner-blocked ] && [ "$(lines "$D/comments-at-pick-2")" = 1 ] \
    && grep -q ' 41 @Adamniels PR #50 needs you, do not merge it as it is: .*ci-ok failed, risk is not low' "$D/comments-at-pick-2" \
    && ! grep -q 'ready' "$D/comments.log" && [ "$(cat "$D/gate-at-pick-2")" = 50 ] \
    && [ "$(cat "$D/labels.log")" = "50 +owner-blocked -in-review" ]; echo $?)

setup; same_turn 2; export FAKE_PR_LABELS=in-review,has-decision
hook claude-hook "echo 'changes aaaa 1 2027-01-15T08:00:00Z' > '$D/verdict'"; runner
check "a changes verdict settles nothing in the same turn" \
  $(no_file "$D/gate.log" && no_file "$D/labels.log" && no_file "$D/comments.log" && [ "$(lines "$D/claude.log")" = 1 ] \
    && [ "$(cat "$D/review-ok-aaaa")" = pending ]; echo $?)
export AGENT_SCRIPTS="$tmp/scripts"; unset FAKE_LOOPS   # from here on the picker is the fake one, with its queue

# The verdict is there, as it is when the owner merges or closes just after the reviewer posted it.
setup; export FAKE_VERDICT="$NONE" FAKE_PR_LABELS=in-review; echo pending > "$D/review-ok-aaaa"
approves "echo MERGED > '$D/state-50'"; pending "review 50 41" IDLE; runner
gone_merged=$(quiet && no_file "$D/gate.log" && no_file "$D/status.log" && [ "$(lines "$D/claude.log")" = 1 ]; echo $?)
setup; export FAKE_VERDICT="$NONE" FAKE_PR_LABELS=in-review; echo pending > "$D/review-ok-aaaa"
approves "echo CLOSED > '$D/state-50'"; pending "review 50 41" IDLE; runner
check "a review of a pull request merged or closed meanwhile settles nothing" \
  $([ "$gone_merged" = 0 ] && quiet && no_file "$D/gate.log" && no_file "$D/status.log" && [ "$(lines "$D/claude.log")" = 1 ]; echo $?)

setup; export FAKE_VERDICT="$NONE"; approves; pending "review 50 41"; runner
check "review-ok is success before the gate is called in the same turn" \
  $([ "$(tr '\n' ' ' < "$D/order.log")" = "status aaaa pending status aaaa success gate 50 " ]; echo $?)

# What the same turn acts on is the pull request as the review left it, not as it was read when the item was picked.
setup; export FAKE_VERDICT="$NONE" FAKE_GATE_EXIT=3 FAKE_GATE_OUT="$READY" FAKE_PR_LABELS=in-review
approves "echo in-review,has-decision > '$D/labels-50'"; pending "review 50 41"; runner
check "the same turn reads the labels as they are after the review: has-decision added meanwhile gives owner-decision" \
  $([ "$(cat "$D/labels.log")" = "50 +owner-decision -in-review" ] && grep -q 'waits for your decision' "$D/comments.log" \
    && ! grep -q 'owner-merge' "$D/gh.log"; echo $?)

setup; export FAKE_VERDICT="$NONE" FAKE_GATE_EXIT=3 FAKE_GATE_OUT='risk is not low\n' FAKE_PR_LABELS=in-review
hook claude-hook "echo bbbb > '$D/head'; echo 'approve bbbb 0 2027-01-15T08:00:00Z' > '$D/verdict'"; pending "review 50 41"; runner
check "the same turn settles the head as it is after the review, when the verdict is for it" \
  $([ "$(cat "$D/gate.log")" = 50 ] && [ "$(cat "$AGENT_HOME/agent-logs/owner-told")" = "50 bbbb" ] \
    && [ "$(cat "$D/review-ok-bbbb")" = success ] && [ "$(tail -n1 "$D/labels.log")" = "50 +owner-merge -in-review" ]; echo $?)

setup; export FAKE_VERDICT="$NONE" FAKE_PR_LABELS=in-review; approves "echo bbbb > '$D/head'"; pending "review 50 41"; runner
check "an approve for a head that moved during the review settles nothing in the same turn" \
  $(no_file "$D/gate.log" && no_file "$D/comments.log" && [ "$(cat "$D/review-ok-bbbb")" = pending ] \
    && ! grep -q 'owner-' "$D/gh.log"; echo $?)

# Failures in the same turn are the runner's or GitHub's, never the slice's.
setup; export FAKE_VERDICT="$NONE" FAKE_GATE_EXIT=1 FAKE_PR_LABELS=in-review; approves; pending "review 50 41"; runner
check "a gate error in the same turn is a transient pause, not a failed review" \
  $([ "$(waits transient)" = 60 ] && [ "$(cat "$D/gate.log")" = 50 ] && no_file "$D/labels.log" && no_file "$D/comments.log" \
    && [ "$(fails 41)" = 0 ] && no_file "$D/board.log"; echo $?)

setup; export FAKE_VERDICT="$NONE" FAKE_GATE_EXIT=3 FAKE_GATE_OUT='risk is not low\n' FAKE_PR_LABELS=in-review FAKE_LABEL_EXIT=1
approves; pending "review 50 41" "merge 50 41"; runner
check "a failed label write in the same turn is a transient pause, retried by the item without a second mention" \
  $([ "$(waits transient)" = "60 120" ] && [ "$(lines "$D/comments.log")" = 1 ] && [ "$(grep -c 'add-label owner-merge' "$D/gh.log")" = 2 ] \
    && [ "$(fails 41)" = 0 ] && no_file "$D/board.log"; echo $?)

setup; export FAKE_VERDICT="$NONE" FAKE_GATE_EXIT=3 FAKE_GATE_OUT='risk is not low\n' FAKE_PR_LABELS=in-review FAKE_COMMENT_EXIT=1
approves; pending "review 50 41"; runner
check "a failed mention in the same turn is a transient pause, with no label and no record of a mention" \
  $([ "$(waits transient)" = 60 ] && [ "$(cat "$D/gate.log")" = 50 ] && no_file "$D/labels.log" \
    && no_file "$AGENT_HOME/agent-logs/owner-told" && [ "$(fails 41)" = 0 ] && no_file "$D/board.log"; echo $?)

# The owner was told about this head before, and a later review of the same head approves it again.
setup; export FAKE_VERDICT="$NONE" AGENT_POLL_SECONDS=7 FAKE_GATE_EXIT=3 FAKE_GATE_OUT='risk is not low\n' FAKE_PR_LABELS=in-review
mkdir -p "$AGENT_HOME/agent-logs"; echo "50 aaaa" > "$AGENT_HOME/agent-logs/owner-told"; approves; pending "review 50 41"; runner
check "the same turn never waits the poll interval, even when the mention for this head was made before" \
  $(no_file "$D/sleep.log" && no_file "$D/comments.log" && [ "$(cat "$D/labels.log")" = "50 +owner-merge -in-review" ]; echo $?)

# A review of the same head coming back after an ok run is no progress; the second failure sets the slice Blocked.
setup; export FAKE_VERDICT="$NONE" FAKE_PR_LABELS=in-review; mkdir -p "$AGENT_HOME/agent-logs/failures"
echo 1 > "$AGENT_HOME/agent-logs/failures/41"; echo "review 50 41 aaaa" > "$AGENT_HOME/agent-logs/last-ok"
approves; pending "review 50 41"; runner
check "a review that sets its slice Blocked settles nothing in the same turn" \
  $(no_file "$D/gate.log" && [ "$(cat "$D/status-41")" = Blocked ] && [ "$(cat "$D/labels.log")" = "50 +owner-blocked -in-review" ] \
    && [ "$(lines "$D/comments.log")" = 1 ] && grep -q 'slice #41 is Blocked' "$D/comments.log"; echo $?)

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
