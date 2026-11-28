#!/usr/bin/env bash
# Offline tests for gate.sh. Each case runs it in a throwaway git repository holding copies of the scripts it
# calls, the real manifest and workflows, and stand-in service folders, with fake cargo, uv, dotnet, npm, npx,
# docker, curl and lsof first on PATH: each logs its call and the folder it ran in, so no case builds, tests or
# starts anything. The last case holds gate.sh to the workflows under .github/workflows/: every command they run
# for a check is in gate.sh, so a step added to CI is not silently missing from the local gate.
# Run: bash scripts/agent/gate.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd -P)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

pass=0 fail=0
check() { # name, condition result
  if [ "$2" = 0 ]; then pass=$((pass + 1)); echo "ok   $1"; else fail=$((fail + 1)); echo "FAIL $1"; fi
}

# The fakes. FAKE_FAIL=<text> fails a call whose logged line contains it; FAKE_DOWN=1 makes curl answer 502.
mkdir -p "$tmp/bin"
cat > "$tmp/bin/fake" <<'FAKE'
#!/usr/bin/env bash
tool="$(basename "$0")"
where="${PWD#"$FAKE_ROOT"}"; where="${where#/}"; [ -n "$where" ] || where=.
line="$tool $* @ $where"
echo "$line" >> "$FAKE_LOG"
if [ -n "${FAKE_FAIL:-}" ] && [[ "$line" == *"$FAKE_FAIL"* ]]; then echo "fake failure: $line"; exit 1; fi
case "$tool $*" in
  "cargo sqlx --version") echo "sqlx-cli 0.9.0" ;;
  curl*) if [ "${FAKE_DOWN:-0}" = 1 ]; then printf 502; else printf 200; fi ;;
  lsof*) ;;
esac
exit 0
FAKE
chmod +x "$tmp/bin/fake"
for t in cargo uv dotnet npm npx docker curl lsof; do ln -s "$tmp/bin/fake" "$tmp/bin/$t"; done
export PATH="$tmp/bin:$PATH" FAKE_LOG="$tmp/calls.log" AGENT_SLEEP=: AGENT_GATE_HEALTH_TRIES=2

# setup: a fresh repository whose main is the copies below, with origin/main at its first commit.
n=0
setup() {
  n=$((n + 1))
  R="$tmp/r$n"
  mkdir -p "$R/scripts/agent" "$R/scripts/ci" "$R/infra" "$R/.github" "$R/e2e/tests"
  R="$(cd "$R" && pwd -P)"   # macOS's /var is /private/var, and the fakes log the physical folder
  cp "$repo/scripts/agent/gate.sh" "$repo/scripts/agent/services.sh" "$repo/scripts/agent/e2e-scope.sh" "$R/scripts/agent/"
  cp "$repo/scripts/ci/changed-areas.sh" "$repo/scripts/ci/frontends.sh" "$R/scripts/ci/"
  cp "$repo/infra/services.json" "$R/infra/"
  cp -R "$repo/.github/workflows" "$R/.github/"
  jq -r '.services[].dir' "$R/infra/services.json" | while IFS= read -r d; do mkdir -p "$R/$d/src"; echo x > "$R/$d/src/main"; done
  mkdir -p "$R/design/ui" "$R/devflow/web"
  echo '{}' > "$R/design/ui/package.json"; echo '{}' > "$R/devflow/web/package.json"
  for f in test_canary test_look test_front_page test_devflow test_devflow_look test_review test_registration; do
    echo "def test_x(): pass" > "$R/e2e/tests/$f.py"
  done
  echo "x" > "$R/e2e/conftest.py"
  # The passwords are recognisable, so a case can tell whether one reached a log.
  { echo "POSTGRES_PASSWORD=pw-super-secret"
    for v in $(cd "$R" && bash scripts/agent/services.sh password-vars); do echo "$v=pw-$v-secret"; done
    echo "PLATFORM_HTTPS_PORT=8443"; } > "$R/infra/.env"
  printf 'infra/.env\ne2e/screenshots/\n' > "$R/.gitignore"
  git -C "$R" init -q -b main
  git -C "$R" -c user.email=t@t -c user.name=t add -A
  git -C "$R" -c user.email=t@t -c user.name=t commit -q -m main
  git -C "$R" update-ref refs/remotes/origin/main HEAD
  git -C "$R" switch -q -c 41-slice
  export FAKE_ROOT="$R" AGENT_GATE_CACHE="$tmp/cache$n" AGENT_GATE_LOGS="$tmp/logs$n"
  unset FAKE_FAIL FAKE_DOWN
}
commit() { git -C "$R" -c user.email=t@t -c user.name=t add -A && git -C "$R" -c user.email=t@t -c user.name=t commit -q -m "$1"; }
gate() { : > "$FAKE_LOG"; out="$(cd "$R" && bash scripts/agent/gate.sh "$@" 2>&1)"; rc=$?; }
called() { grep -qF -- "$1" "$FAKE_LOG"; }
said() { grep -qxF -- "$1" <<<"$out"; }

# --- Which areas run --------------------------------------------------------------------------------------------
setup; echo y >> "$R/canary/src/main"; commit canary; gate
check "a canary change runs canary's checks and e2e, and no check of the core, the brain or C#" \
  $([ "$rc" = 0 ] && said "ok canary" && said "ok e2e" && called "uv run ruff check @ canary" \
    && called "uv run ruff format --check @ canary" && called "uv run pytest @ canary" \
    && ! called "cargo fmt" && ! called "uv run mypy" && ! called "dotnet build" && said "gate: passed"; echo $?)
check "its e2e runs only canary's tests and the shared ones, against the dev stack's origin" \
  $(called "uv run pytest --base-url https://platform.test:8443 tests/test_canary.py tests/test_front_page.py tests/test_look.py @ e2e" \
    && called "uv run ruff check @ e2e" && called "uv run ruff format --check @ e2e"; echo $?)
check "the platform is started first: frontends built, Caddy recreated, every service migrated" \
  $(called "npm ci @ design/ui" && called "npm run build @ devflow/web" \
    && called "docker compose up -d --wait --force-recreate caddy @ infra" && called "cargo run --locked -- migrate @ Core" \
    && called "dotnet run --project src/DevFlow -c Release --no-launch-profile -- migrate @ devflow"; echo $?)

setup; echo y >> "$R/devflow/src/main"; commit devflow; gate
check "a C# service's change runs its dotnet steps in its folder, and its migrations" \
  $([ "$rc" = 0 ] && said "ok dotnet-devflow" && ! said "ok dotnet-review" && called "dotnet restore --locked-mode @ devflow" \
    && called "dotnet format --verify-no-changes --no-restore @ devflow" && called "dotnet build --no-restore -c Release @ devflow" \
    && called "dotnet test --no-build --no-restore -c Release @ devflow"; echo $?)

setup; mkdir -p "$R/infra/hosted"; echo y > "$R/infra/hosted/compose.yaml"; commit hosted; gate
check "the hosted check is never run here: it is left to CI" \
  $(said "skip hosted (CI's hosted job runs infra/hosted/check.sh)" && ! grep -q "check.sh" "$FAKE_LOG"; echo $?)

setup; echo y >> "$R/Core/src/main"; commit core; gate
check "a core change runs the core's steps and the whole e2e suite" \
  $([ "$rc" = 0 ] && said "ok core" && called "cargo fmt --check @ Core" && called "cargo clippy --locked --all-targets -- -D warnings @ Core" \
    && called "cargo test --locked @ Core" && called "cargo test --locked --test app_roles -- --ignored @ Core" \
    && called "cargo sqlx prepare --check -- --all-targets --locked @ Core" \
    && called "uv run pytest --base-url https://platform.test:8443 @ e2e"; echo $?)

setup; gate
check "a branch with no change runs nothing and passes" $([ "$rc" = 0 ] && [ ! -s "$FAKE_LOG" ] && said "gate: passed"; echo $?)

# --- Uncommitted work is gated --------------------------------------------------------------------------------------
setup; echo y >> "$R/canary/src/main"; echo new > "$R/canary/src/new"; gate
check "uncommitted and untracked changes are gated, so a task is gated before its commit" \
  $(said "ok canary" && called "uv run pytest @ canary"; echo $?)

# --- The cache ------------------------------------------------------------------------------------------------------
setup; echo y >> "$R/canary/src/main"; echo y >> "$R/devflow/src/main"; commit both; gate; gate
check "a second run with nothing changed runs nothing and passes from the cache" \
  $([ "$rc" = 0 ] && said "cached canary" && said "cached dotnet-devflow" && said "cached e2e" && [ ! -s "$FAKE_LOG" ]; echo $?)
echo z >> "$R/devflow/src/main"; gate
check "a change in one service runs that service again and e2e, and keeps the other cached" \
  $([ "$rc" = 0 ] && said "ok dotnet-devflow" && said "cached canary" && said "ok e2e" && ! called "uv run pytest @ canary"; echo $?)
gate --no-cache
check "--no-cache runs every area" $(said "ok canary" && said "ok dotnet-devflow" && said "ok e2e"; echo $?)

# --- Failures -------------------------------------------------------------------------------------------------------
setup; echo y >> "$R/canary/src/main"; echo y >> "$R/devflow/src/main"; commit both
export FAKE_FAIL="ruff format --check @ canary"; gate
check "a failing step fails its area with its log, the other areas still run, and the gate exits 1" \
  $([ "$rc" = 1 ] && grep -q "^FAIL canary (log: " <<<"$out" && grep -q "fake failure: uv run ruff format --check" <<<"$out" \
    && ! called "uv run pytest @ canary" && said "ok dotnet-devflow" && said "ok e2e" && said "gate: failed"; echo $?)
unset FAKE_FAIL; gate
check "a failed area is not cached: the next run runs it again" \
  $([ "$rc" = 0 ] && said "ok canary" && said "cached dotnet-devflow"; echo $?)

setup; echo y >> "$R/canary/src/main"; commit canary; export FAKE_DOWN=1; gate
check "a platform that never answers fails e2e at once, without running the suite" \
  $([ "$rc" = 1 ] && grep -q "^FAIL e2e" <<<"$out" && grep -q "never answered 200" <<<"$out" && ! called "uv run pytest --base-url"; echo $?)

setup; echo y >> "$R/Core/src/main"; commit core; gate
check "no password reaches the output or a log" \
  $(! grep -q -- "-secret" <<<"$out" && ! grep -rq -- "-secret" "$AGENT_GATE_LOGS"; echo $?)

# --- The platform alone, for the before screenshots -----------------------------------------------------------
setup; gate --start-platform
check "--start-platform starts the platform from the branch and leaves it running, running no area" \
  $([ "$rc" = 0 ] && called "docker compose up -d --wait --force-recreate caddy @ infra" && called "cargo run --locked -q @ Core" \
    && [ "$(grep -c "^lsof -ti tcp:8080" "$FAKE_LOG")" = 2 ] && ! called "uv run pytest" && ! grep -q "^ok " <<<"$out"; echo $?)
export FAKE_DOWN=1; gate --start-platform
check "--start-platform exits 1 when the platform never answers" $([ "$rc" = 1 ] && grep -q "never answered 200" <<<"$out"; echo $?)
unset FAKE_DOWN; gate --stop-platform
check "--stop-platform stops what listens on each service's port, and nothing else" \
  $([ "$rc" = 0 ] && called "lsof -ti tcp:8084 -sTCP:LISTEN" && ! called "docker" && ! called "cargo"; echo $?)

setup; echo y >> "$R/canary/src/main"; commit canary; gate --bogus
check "an unknown option is a usage error" $([ "$rc" = 2 ]; echo $?)

# --- gate.sh runs what CI runs ----------------------------------------------------------------------------------------
# Every command a workflow runs for a check: a single line `run:`, or a line of a `run: |` block, that starts with
# cargo, uv run, npm, npx or dotnet. Setup that gate.sh leaves to the machine or does another way is listed below.
skip='^(rustup toolchain install|cargo install sqlx-cli .*|cargo run --locked -- migrate|uv run brain migrate|uv run playwright install --with-deps chromium|cargo run --locked -q -- keygen >> \.env)$'
missing=""
for wf in core canary brain libs dotnet web e2e; do
  while IFS= read -r cmd; do
    [ -n "$cmd" ] || continue
    grep -Eq "$skip" <<<"$cmd" && continue
    grep -qF -- "$cmd" "$here/gate.sh" || missing="$missing
     $wf: $cmd"
  done < <(sed -E -n 's/^[[:space:]]*(run: )?((cargo|uv run|npm|npx|dotnet) [^&|;]*[^&|; ]).*$/\2/p' "$repo/.github/workflows/$wf.yml" | sort -u)
done
[ -z "$missing" ] || echo "     not in gate.sh:$missing"
check "every check command in the workflows is one gate.sh runs" $([ -z "$missing" ]; echo $?)

echo "$pass passed, $fail failed"
[ "$fail" = 0 ]
