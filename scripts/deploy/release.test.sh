#!/usr/bin/env bash
# Offline tests for release.sh. It runs a copy of the script in a fake repository holding the files
# it reads, with fake `docker`, `git` and `curl` first on PATH that log every call and answer from
# settings the test gives them, so no test touches Docker, a remote or the network.
# Run: bash scripts/deploy/release.test.sh
# Written for bash 3.2, the one macOS ships.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd -P)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
pass=0
fail=0

ok() { echo "ok   $1"; pass=$((pass + 1)); }
bad() { echo "FAIL $1"; [ -n "${2:-}" ] && printf '     %s\n' "$2"; fail=$((fail + 1)); }

# --- The fakes. -----------------------------------------------------------------------------------
#
# Each appends one line to $FAKE_LOG, `<command> <arguments>`, with PLATFORM_RELEASE when docker is
# called, so a test can read which images a compose command used.
#
# docker: FAKE_DOCKER_FAIL=<shell pattern> fails any call whose arguments match it. `compose port`
# answers 127.0.0.1:9080; `compose ps` lists FAKE_SERVICES; `image ls` lists FAKE_IMAGES; `compose
# run --rm backup now` prints the lines the backup job prints, for the time 20261115T020004Z.
#
# git: HEAD is FAKE_HEAD_REF, a branch name, or detached. `rev-parse --verify` resolves origin/main
# and any 40 character sha to itself, sha $MAIN for origin/main and $OLD and $PREV as themselves, and
# fails for anything else; `merge-base --is-ancestor` succeeds for each sha in FAKE_ON_MAIN;
# `status --porcelain` prints FAKE_DIRTY; `diff --quiet` exits FAKE_DIFF_EXIT.
#
# curl: FAKE_HEALTH_CODE, 200 unless set, for every call; FAKE_HEALTH_CODE_AFTER_ROLLBACK, when set,
# for every call after a compose up made with the previous release's tag.
mkdir -p "$tmp/bin"
cat > "$tmp/bin/docker" << 'EOF'
#!/usr/bin/env bash
echo "docker PLATFORM_RELEASE=${PLATFORM_RELEASE:-} $*" >> "$FAKE_LOG"
if [ -n "${FAKE_DOCKER_FAIL:-}" ]; then
  case "$*" in $FAKE_DOCKER_FAIL) echo "fake docker failing: $*" >&2; exit 1 ;; esac
fi
# Every secret the env file holds is printed by a call, so a test can see whether the script lets
# a command's output through unread: it must not print it.
case "$*" in
  "compose "*" port proxy 80") echo "127.0.0.1:9080" ;;
  "compose "*" ps --format "*)
    for s in $FAKE_SERVICES; do printf '%s\tplatform-hosted/%s:%s\tUp 3 seconds\n' "$s" "$s" "${PLATFORM_RELEASE:-dev}"; done ;;
  "image ls "*) for i in ${FAKE_IMAGES:-}; do echo "$i"; done ;;
  "compose "*" run --rm -T backup now")
    echo "backup: dumped brain"
    echo "backup: uploaded 20261115T020004Z-brain.dump.age"
    echo "backup: dumped core"
    echo "backup: uploaded 20261115T020004Z-core.dump.age"
    echo "backup: 2026-11-15T02:00:04Z, 2 databases dumped and uploaded" ;;
  "compose "*" up -d")
    [ "${PLATFORM_RELEASE:-}" = "${FAKE_PREV_TAG:-none}" ] && touch "$FAKE_DIR/rolled-back" ;;
esac
exit 0
EOF
cat > "$tmp/bin/git" << 'EOF'
#!/usr/bin/env bash
echo "git $*" >> "$FAKE_LOG"
case "$*" in
  "symbolic-ref -q --short HEAD")
    [ -n "${FAKE_HEAD_REF:-}" ] && { echo "$FAKE_HEAD_REF"; exit 0; }
    exit 1 ;;
  "rev-parse --verify --quiet HEAD^{commit}") echo "$FAKE_HEAD_SHA" ;;
  "rev-parse --verify --quiet origin/main^{commit}") echo "$FAKE_MAIN" ;;
  "rev-parse --verify --quiet "*"^{commit}")
    c="${4%"^{commit}"}"
    case "$c" in *[!0-9a-f]* | "") exit 1 ;; esac
    [ "${#c}" -eq 40 ] || exit 1
    echo "$c" ;;
  "merge-base --is-ancestor "*)
    case " ${FAKE_ON_MAIN:-} " in *" $3 "*) exit 0 ;; esac
    exit 1 ;;
  "status --porcelain") printf '%s' "${FAKE_DIRTY:-}" ;;
  "diff --quiet "*) exit "${FAKE_DIFF_EXIT:-0}" ;;
esac
exit 0
EOF
cat > "$tmp/bin/curl" << 'EOF'
#!/usr/bin/env bash
echo "curl $*" >> "$FAKE_LOG"
if [ -e "$FAKE_DIR/rolled-back" ] && [ -n "${FAKE_HEALTH_CODE_AFTER_ROLLBACK:-}" ]; then
  printf '%s' "$FAKE_HEALTH_CODE_AFTER_ROLLBACK"
else
  printf '%s' "${FAKE_HEALTH_CODE:-200}"
fi
EOF
chmod +x "$tmp/bin/docker" "$tmp/bin/git" "$tmp/bin/curl"

# --- The fake repository. -------------------------------------------------------------------------

repo="$tmp/repo"
mkdir -p "$repo/scripts/deploy" "$repo/scripts/agent" "$repo/infra/hosted/postgres" "$repo/infra/postgres/initdb"
cp "$root/scripts/deploy/release.sh" "$repo/scripts/deploy/"
cp "$root/scripts/agent/services.sh" "$repo/scripts/agent/"
cp "$root/infra/services.json" "$repo/infra/"
cp "$root/infra/hosted/compose.yaml" "$root/infra/hosted/.env.example" "$repo/infra/hosted/"
cp "$root/infra/hosted/postgres/backup-role.sh" "$repo/infra/hosted/postgres/"
cp "$root/infra/postgres/initdb/"*.sh "$repo/infra/postgres/initdb/"

MAIN=1111111111111111111111111111111111111111
PREV=2222222222222222222222222222222222222222
OLD=3333333333333333333333333333333333333333
BRANCH=4444444444444444444444444444444444444444
HEADSHA=5555555555555555555555555555555555555555

# Every value is a marker the output is searched for; the passwords are letters and digits.
secret_value() { echo "secret$(echo "$1" | tr -dc 'A-Za-z0-9')Xyz"; }

# env_file [<NAME>=<value>]...: the env file every variable .env.example names set, the
# overrides applied, a value of - removing the line.
env_file() {
  local file="$repo/infra/hosted/.env" var override name value
  : > "$file"
  for var in $(sed -nE 's/^([A-Z_][A-Z0-9_]*)=.*/\1/p' "$repo/infra/hosted/.env.example") CORE_SIGNING_KEY; do
    case "$var" in
      PLATFORM_PUBLIC_ORIGIN) value="https://platform.example.ts.net" ;;
      *) value="$(secret_value "$var")" ;;
    esac
    for override in "$@"; do
      name="${override%%=*}"
      [ "$name" = "$var" ] && value="${override#*=}"
    done
    [ "$value" = - ] && continue
    echo "$var=$value" >> "$file"
  done
}

# run [<argument>]...: one release, from /tmp so the script must find its root itself, with the
# fakes' settings from the environment the test sets. Its exit code is in $rc, its output in out.
run() {
  rm -f "$tmp/log" "$tmp/rolled-back"
  : > "$tmp/log"
  (cd /tmp && FAKE_LOG="$tmp/log" FAKE_DIR="$tmp" FAKE_MAIN="$MAIN" FAKE_HEAD_SHA="$HEADSHA" \
    RELEASE_HEALTH_SECONDS=0 PATH="$tmp/bin:$PATH" bash "$repo/scripts/deploy/release.sh" "$@" > "$tmp/out" 2>&1)
  rc=$?
}

reset_state() {
  env_file
  rm -f "$repo/infra/hosted/.release"
  unset FAKE_DOCKER_FAIL FAKE_DIRTY FAKE_DIFF_EXIT FAKE_HEALTH_CODE FAKE_HEALTH_CODE_AFTER_ROLLBACK \
    FAKE_PREV_TAG FAKE_IMAGES HOSTED_PROJECT HOSTED_ENV_FILE
  export FAKE_ON_MAIN="$MAIN $PREV $OLD" FAKE_HEAD_REF=main
  export FAKE_SERVICES="postgres core canary brain review proxy backup"
}

calls() { cat "$tmp/log"; }
out() { cat "$tmp/out"; }
# first_line <pattern>: the number of the first log line matching the extended regex, 0 if none.
first_line() { grep -nE -- "$1" "$tmp/log" | head -n 1 | cut -d: -f1 | grep . || echo 0; }
started_or_stopped() { grep -E '^docker .* compose .* (up|down|stop|restart|start|rm|kill)( |$)' "$tmp/log"; }

# --- the_steps_run_in_order -----------------------------------------------------------------------

reset_state
run
order=""
prev_line=0
in_order=1
for step in \
  '^docker .* info' \
  '^git fetch origin' \
  "^git checkout --quiet --detach $MAIN" \
  '^docker PLATFORM_RELEASE=111111111111 compose .* build' \
  '^docker .* compose .* up -d --wait postgres' \
  '^docker .* compose .* exec -T postgres bash /docker-entrypoint-initdb.d/brain.sh' \
  '^docker .* compose .* exec -T postgres bash /docker-entrypoint-initdb.d/review.sh' \
  '^docker .* compose .* exec -T postgres bash /docker-entrypoint-initdb.d/zz-backup-role.sh' \
  '^docker PLATFORM_RELEASE=111111111111 compose .* run --rm -T backup now' \
  '^docker PLATFORM_RELEASE=111111111111 compose .* run --rm -T core-migrate' \
  '^docker PLATFORM_RELEASE=111111111111 compose .* run --rm -T brain-migrate' \
  '^docker PLATFORM_RELEASE=111111111111 compose .* run --rm -T review-migrate' \
  '^docker PLATFORM_RELEASE=111111111111 compose .* up -d$' \
  '^curl .*http://127.0.0.1:9080/login' \
  '^curl .*http://127.0.0.1:9080/review/api/health'; do
  line="$(first_line "$step")"
  order="$order $line"
  if [ "$line" -eq 0 ] || [ "$line" -le "$prev_line" ]; then in_order=0; fi
  prev_line="$line"
done
headers="$(grep -E '^==> ' "$tmp/out" | sed -E 's/^==> ([0-9]+)\..*/\1/' | tr '\n' ' ')"
if [ "$rc" -eq 0 ] && [ "$in_order" -eq 1 ] && [ "$headers" = "1 2 3 4 5 6 7 8 9 " ] &&
  [ "$(cat "$repo/infra/hosted/.release" 2> /dev/null)" = "$MAIN" ]; then
  ok the_steps_run_in_order
else
  bad the_steps_run_in_order "rc=$rc, lines:$order, headers: $headers; $(out | tail -n 5 | tr '\n' ' ')"
fi

# Every compose command names the project, the env file and the hosted compose file, and nothing
# else as a compose file.
wrong="$(grep -E '^docker .* compose ' "$tmp/log" |
  grep -vE "^docker PLATFORM_RELEASE=[a-z0-9]* compose -p platform-hosted --env-file infra/hosted/.env -f infra/hosted/compose.yaml( |$)")"
if [ -z "$wrong" ] && grep -q ' compose ' "$tmp/log"; then
  ok every_compose_command_names_the_project_the_env_file_and_the_compose_file
else
  bad every_compose_command_names_the_project_the_env_file_and_the_compose_file "$wrong"
fi

# --- A commit is the argument, and must be on main. -----------------------------------------------

reset_state
run "$OLD"
if [ "$rc" -eq 0 ] && grep -q "^git merge-base --is-ancestor $OLD origin/main" "$tmp/log" &&
  grep -q "^git checkout --quiet --detach $OLD" "$tmp/log" &&
  grep -q '^docker PLATFORM_RELEASE=333333333333 compose .* build' "$tmp/log"; then
  ok an_older_commit_on_main_is_released
else
  bad an_older_commit_on_main_is_released "rc=$rc; $(out | tail -n 3 | tr '\n' ' ')"
fi

reset_state
run "$BRANCH"
if [ "$rc" -ne 0 ] && ! grep -qE '^git checkout' "$tmp/log" && ! grep -qE ' build' "$tmp/log" &&
  grep -q 'not on origin/main' "$tmp/out"; then
  ok a_commit_that_is_not_on_main_is_refused
else
  bad a_commit_that_is_not_on_main_is_refused "rc=$rc; $(calls | grep -E 'checkout|build' | tr '\n' ' ')"
fi

reset_state
run not-a-commit
if [ "$rc" -ne 0 ] && ! grep -qE '^git checkout' "$tmp/log" && ! grep -qE ' build' "$tmp/log"; then
  ok a_commit_that_does_not_exist_is_refused
else
  bad a_commit_that_does_not_exist_is_refused "rc=$rc"
fi

reset_state
run --upload-pack=evil
if [ "$rc" -eq 2 ] && ! grep -q '^docker\|^git' "$tmp/log"; then
  ok an_option_is_not_taken_for_a_commit
else
  bad an_option_is_not_taken_for_a_commit "rc=$rc; $(calls | tr '\n' ' ')"
fi

# --- Preflight. -----------------------------------------------------------------------------------

reset_state
export FAKE_DIRTY=" M Core/src/lib.rs"
run
if [ "$rc" -ne 0 ] && ! grep -qE '^git (fetch|checkout)' "$tmp/log" && ! grep -qE ' build' "$tmp/log" &&
  grep -q 'working tree has changes' "$tmp/out"; then
  ok a_dirty_tree_is_refused
else
  bad a_dirty_tree_is_refused "rc=$rc; $(out | tr '\n' ' ')"
fi

reset_state
export FAKE_DOCKER_FAIL="info*"
run
if [ "$rc" -ne 0 ] && ! grep -qE '^git (fetch|checkout)' "$tmp/log" && grep -q 'Docker does not answer' "$tmp/out"; then
  ok docker_not_answering_is_refused
else
  bad docker_not_answering_is_refused "rc=$rc; $(out | tr '\n' ' ')"
fi

reset_state
rm -f "$repo/infra/hosted/.env"
run
if [ "$rc" -ne 0 ] && ! grep -qE '^git (fetch|checkout)' "$tmp/log" && grep -q 'infra/hosted/.env does not exist' "$tmp/out"; then
  ok a_missing_env_file_is_refused
else
  bad a_missing_env_file_is_refused "rc=$rc; $(out | tr '\n' ' ')"
fi

# a_missing_or_empty_setting_is_refused_naming_it, for each variable .env.example names, and the
# signing key, each missing and each empty.
missed=""
for var in $(sed -nE 's/^([A-Z_][A-Z0-9_]*)=.*/\1/p' "$repo/infra/hosted/.env.example") CORE_SIGNING_KEY; do
  for value in - ""; do
    reset_state
    env_file "$var=$value"
    run
    if [ "$rc" -eq 0 ] || grep -qE '^git (fetch|checkout)' "$tmp/log" || ! grep -q "$var" "$tmp/out"; then
      missed="$missed $var=${value:-empty}"
    fi
  done
done
reset_state
env_file "CORE_APP_PASSWORD=abc-def"
run
if [ "$rc" -eq 0 ] || ! grep -q 'CORE_APP_PASSWORD' "$tmp/out" || grep -q 'abc-def' "$tmp/out"; then
  missed="$missed CORE_APP_PASSWORD=abc-def"
fi
if [ -z "$missed" ]; then
  ok a_missing_or_empty_setting_is_refused_naming_it
else
  bad a_missing_or_empty_setting_is_refused_naming_it "not refused, or not named:$missed"
fi

# A value exported in the caller's shell does not stand in for one missing from the file, since
# compose would read it; the file alone counts.
reset_state
env_file "BACKUP_S3_BUCKET=-"
BACKUP_S3_BUCKET=from-the-shell run
if [ "$rc" -ne 0 ] && grep -q BACKUP_S3_BUCKET "$tmp/out"; then
  ok the_env_file_is_the_only_source_of_settings
else
  bad the_env_file_is_the_only_source_of_settings "rc=$rc"
fi

reset_state
env_file "PLATFORM_PUBLIC_ORIGIN=http://platform.example.ts.net"
run
if [ "$rc" -ne 0 ] && ! grep -qE '^git (fetch|checkout)' "$tmp/log" && grep -q 'PLATFORM_PUBLIC_ORIGIN' "$tmp/out"; then
  ok an_origin_that_is_not_https_is_refused
else
  bad an_origin_that_is_not_https_is_refused "rc=$rc; $(out | tr '\n' ' ')"
fi

# --- A failed step before the start. -------------------------------------------------------------

reset_state
export FAKE_DOCKER_FAIL="compose * build"
run
if [ "$rc" -ne 0 ] && [ -z "$(started_or_stopped)" ] && ! grep -q 'backup now\|-migrate' "$tmp/log" &&
  [ ! -e "$repo/infra/hosted/.release" ] && grep -q '^git checkout --quiet main' "$tmp/log"; then
  ok a_failed_build_changes_nothing
else
  bad a_failed_build_changes_nothing "rc=$rc; $(started_or_stopped | tr '\n' ' ')"
fi

reset_state
export FAKE_DOCKER_FAIL="compose * run --rm -T backup now"
run
if [ "$rc" -ne 0 ] && ! grep -q -- '-migrate' "$tmp/log" && ! grep -qE ' up -d$' "$tmp/log" &&
  grep -q 'backup failed' "$tmp/out"; then
  ok a_failed_backup_stops_before_any_migration
else
  bad a_failed_backup_stops_before_any_migration "rc=$rc; $(out | tail -n 3 | tr '\n' ' ')"
fi

reset_state
echo "$PREV" > "$repo/infra/hosted/.release"
export FAKE_DOCKER_FAIL="compose * run --rm -T brain-migrate"
run
if [ "$rc" -ne 0 ] && ! grep -qE ' up -d$' "$tmp/log" && ! grep -q 'review-migrate' "$tmp/log" &&
  [ -z "$(started_or_stopped | grep -v 'up -d --wait postgres')" ] &&
  grep -q 'brain-migrate failed' "$tmp/out" &&
  [ "$(cat "$repo/infra/hosted/.release")" = "$PREV" ] && grep -q '^git checkout --quiet main' "$tmp/log"; then
  ok a_failed_migration_leaves_the_old_containers_serving
else
  bad a_failed_migration_leaves_the_old_containers_serving "rc=$rc; $(started_or_stopped | tr '\n' ' '); $(out | tail -n 3 | tr '\n' ' ')"
fi

# --- A failed health check. -----------------------------------------------------------------------

reset_state
echo "$PREV" > "$repo/infra/hosted/.release"
export FAKE_HEALTH_CODE=502 FAKE_HEALTH_CODE_AFTER_ROLLBACK=200 FAKE_DIFF_EXIT=0 FAKE_PREV_TAG=222222222222
run
if [ "$rc" -ne 0 ] && grep -qE "^git diff --quiet $PREV $MAIN -- .*migrations" "$tmp/log" &&
  grep -q "^git checkout --quiet --detach $PREV" "$tmp/log" &&
  grep -qE '^docker PLATFORM_RELEASE=222222222222 compose .* up -d$' "$tmp/log" &&
  [ "$(cat "$repo/infra/hosted/.release")" = "$PREV" ] && grep -q 'rolled back' "$tmp/out"; then
  ok a_failed_health_check_rolls_back_when_no_migration_changed
else
  bad a_failed_health_check_rolls_back_when_no_migration_changed "rc=$rc; $(out | tail -n 4 | tr '\n' ' ')"
fi

# The rollback's own health failing is said, and still exits non zero.
reset_state
echo "$PREV" > "$repo/infra/hosted/.release"
export FAKE_HEALTH_CODE=502 FAKE_DIFF_EXIT=0 FAKE_PREV_TAG=222222222222
run
if [ "$rc" -ne 0 ] && grep -q 'previous release did not answer either' "$tmp/out" &&
  [ "$(cat "$repo/infra/hosted/.release")" = "$PREV" ]; then
  ok a_rollback_that_fails_its_health_check_says_so
else
  bad a_rollback_that_fails_its_health_check_says_so "rc=$rc; $(out | tail -n 4 | tr '\n' ' ')"
fi

reset_state
echo "$PREV" > "$repo/infra/hosted/.release"
export FAKE_HEALTH_CODE=502 FAKE_DIFF_EXIT=1 FAKE_PREV_TAG=222222222222
run
if [ "$rc" -ne 0 ] && [ ! -e "$tmp/rolled-back" ] && ! grep -q "^git checkout --quiet --detach $PREV" "$tmp/log" &&
  grep -q 'database is ahead of the previous version' "$tmp/out" &&
  grep -q '20261115T020004Z' "$tmp/out" && grep -q 'restore' "$tmp/out" &&
  [ "$(cat "$repo/infra/hosted/.release")" = "$PREV" ] && [ -z "$(grep -E ' (down|stop)( |$)' "$tmp/log")" ]; then
  ok a_failed_health_check_does_not_roll_back_across_a_migration
else
  bad a_failed_health_check_does_not_roll_back_across_a_migration "rc=$rc; $(out | tail -n 6 | tr '\n' ' ')"
fi

# A diff that cannot be read is not taken for no change.
reset_state
echo "$PREV" > "$repo/infra/hosted/.release"
export FAKE_HEALTH_CODE=502 FAKE_DIFF_EXIT=128 FAKE_PREV_TAG=222222222222
run
if [ "$rc" -ne 0 ] && [ ! -e "$tmp/rolled-back" ] && grep -q '20261115T020004Z' "$tmp/out"; then
  ok a_migration_diff_that_fails_is_not_rolled_back_across
else
  bad a_migration_diff_that_fails_is_not_rolled_back_across "rc=$rc; $(out | tail -n 4 | tr '\n' ' ')"
fi

reset_state
export FAKE_HEALTH_CODE=502
run
if [ "$rc" -ne 0 ] && grep -q 'nothing to roll back to' "$tmp/out" && grep -q ' logs' "$tmp/out" &&
  [ -z "$(grep -E ' (down|stop|rm)( |$)' "$tmp/log")" ] && [ ! -e "$repo/infra/hosted/.release" ]; then
  ok the_first_release_has_nothing_to_roll_back_to
else
  bad the_first_release_has_nothing_to_roll_back_to "rc=$rc; $(out | tail -n 4 | tr '\n' ' ')"
fi

# --- A successful release. ------------------------------------------------------------------------

reset_state
echo "$PREV" > "$repo/infra/hosted/.release"
export FAKE_IMAGES="platform-hosted/core:111111111111 platform-hosted/core:222222222222 platform-hosted/core:333333333333 platform-hosted/core:check platform-hosted/brain:333333333333"
run
named=1
for s in $FAKE_SERVICES; do grep -q "$s" "$tmp/out" || named=0; done
if [ "$rc" -eq 0 ] && [ "$(cat "$repo/infra/hosted/.release")" = "$MAIN" ] && grep -q "$MAIN" "$tmp/out" &&
  [ "$named" -eq 1 ]; then
  ok a_successful_release_is_recorded_and_printed
else
  bad a_successful_release_is_recorded_and_printed "rc=$rc; $(out | tail -n 10 | tr '\n' ' ')"
fi

removed="$(grep -E '^docker .* image rm' "$tmp/log" | sed -E 's/.* image rm //' | tr '\n' ' ')"
if [ "$removed" = "platform-hosted/core:333333333333 platform-hosted/brain:333333333333 " ]; then
  ok only_images_older_than_the_previous_release_are_removed
else
  bad only_images_older_than_the_previous_release_are_removed "removed: $removed"
fi

# --- --local. -------------------------------------------------------------------------------------

reset_state
run --local
if [ "$rc" -eq 2 ] && ! grep -q '^docker\|^git' "$tmp/log"; then
  ok local_mode_is_refused_for_the_real_project
else
  bad local_mode_is_refused_for_the_real_project "rc=$rc; $(calls | tr '\n' ' ')"
fi

reset_state
cp "$repo/infra/hosted/.env" "$tmp/check.env"
echo "PLATFORM_RELEASE=check" >> "$tmp/check.env"
export FAKE_DIRTY=" M Core/src/lib.rs" FAKE_IMAGES="platform-hosted/core:333333333333"
echo "$PREV" > "$repo/infra/hosted/.release"
HOSTED_PROJECT=hostedcheck HOSTED_ENV_FILE="$tmp/check.env" run --local
wrong="$(grep -E '^docker .* compose ' "$tmp/log" |
  grep -vE "^docker PLATFORM_RELEASE=check compose -p hostedcheck --env-file $tmp/check.env -f infra/hosted/compose.yaml( |$)")"
if [ "$rc" -eq 0 ] && [ -z "$wrong" ] && ! grep -qE '^git (fetch|checkout|diff)' "$tmp/log" &&
  ! grep -q 'image rm' "$tmp/log" && grep -qE ' up -d$' "$tmp/log" &&
  [ "$(cat "$repo/infra/hosted/.release")" = "$PREV" ]; then
  ok local_mode_builds_the_tree_as_it_is_under_its_own_project_and_tag
else
  bad local_mode_builds_the_tree_as_it_is_under_its_own_project_and_tag "rc=$rc; $wrong; $(out | tail -n 3 | tr '\n' ' ')"
fi

reset_state
run --local "$MAIN"
if [ "$rc" -eq 2 ] && ! grep -q '^docker\|^git' "$tmp/log"; then
  ok local_mode_takes_no_commit
else
  bad local_mode_takes_no_commit "rc=$rc"
fi

reset_state
HOSTED_PROJECT='Bad Name' run
if [ "$rc" -eq 2 ] && ! grep -q '^docker\|^git' "$tmp/log"; then
  ok a_project_name_compose_would_refuse_is_refused_first
else
  bad a_project_name_compose_would_refuse_is_refused_first "rc=$rc"
fi

# --- no_secret_is_printed, over every run above and a fresh one of each outcome, and no secret on a
# command line of any fake. --------------------------------------------------------------------------

leaked=""
check_secrets() {
  local var value
  for var in $(sed -nE 's/^([A-Z_][A-Z0-9_]*)=.*/\1/p' "$repo/infra/hosted/.env.example") CORE_SIGNING_KEY; do
    [ "$var" = PLATFORM_PUBLIC_ORIGIN ] && continue
    value="$(secret_value "$var")"
    if grep -qF -- "$value" "$tmp/out" "$tmp/log"; then leaked="$leaked $1:$var"; fi
  done
}
reset_state
run
check_secrets success
reset_state
echo "$PREV" > "$repo/infra/hosted/.release"
FAKE_HEALTH_CODE=502 FAKE_DIFF_EXIT=1 run
check_secrets no-rollback
reset_state
FAKE_DOCKER_FAIL="compose * run --rm -T core-migrate" run
check_secrets migration
reset_state
env_file "CORE_APP_PASSWORD=$(secret_value CORE_APP_PASSWORD)-x"
run
check_secrets bad-password
if [ -z "$leaked" ]; then
  ok no_secret_is_printed
else
  bad no_secret_is_printed "printed or passed:$leaked"
fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
