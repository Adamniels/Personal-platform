#!/usr/bin/env bash
# Checks that CI tests the committed lockfiles: every cargo command in a workflow that resolves
# dependencies passes --locked, every job that runs `uv run` sets UV_LOCKED to 1, and npm installs
# only with `npm ci`, and every `dotnet restore` passes --locked-mode while `dotnet build` and
# `dotnet test` pass --no-restore, so the one restore is the locked one. The commands in infra/services.json, which e2e.yml and the agents run through
# scripts/agent/services.sh, are held to the same: cargo with --locked, and `uv run` with
# UV_LOCKED=1 in front, since they also run outside any job's env. Without them cargo, uv and npm resolve again when a lockfile does not match
# its manifest, and the push goes green. Every Dockerfile in the repository is held to the rules
# infra/hosted/tests/agree.test.sh holds them to, through the same scripts/ci/dockerfile.sh, so
# the scripts job checks them on every pull request and not only when the hosted check runs.
# The same checks run on sample workflows, so they cannot pass by matching nothing.
# Run: bash scripts/ci/workflows-locked.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
workflows="$here/../../.github/workflows"
manifest="$here/../../infra/services.json"
pass=0
fail=0

# cargo_unlocked <file>...: prints "checked <n>", then one line per cargo command that should pass
# --locked and does not. `cargo run`, `cargo test` and `cargo clippy` need it before the first
# `--`, since what follows goes to the binary or the test harness. `cargo sqlx prepare` has no
# such flag and passes what follows `--` to cargo, so there it goes after the `--`. A command ends
# at a shell separator or a redirection. Comment lines are skipped.
cargo_unlocked() {
  awk '
    function ends(t) { return t ~ /^(;|&&|\|\||\||&)$/ || t ~ /^[0-9]*[<>]/ }
    /^[ \t]*#/ { next }
    {
      n = split($0, t, /[ \t]+/)
      for (i = 1; i <= n; i++) {
        if (t[i] != "cargo") continue
        if (t[i + 1] ~ /^(run|test|clippy)$/) { sub_ = t[i + 1]; j = i + 2; after = 0 }
        else if (t[i + 1] == "sqlx" && t[i + 2] == "prepare") { sub_ = "sqlx prepare"; j = i + 3; after = 1 }
        else continue
        checked++
        seen = 0; locked = 0
        for (; j <= n && !ends(t[j]); j++) {
          if (t[j] == "--") { seen = 1; continue }
          if (t[j] == "--locked" && seen == after) locked = 1
        }
        if (!locked) printf "%s:%d: cargo %s without --locked %s the --: %s\n", FILENAME, FNR, sub_, (after ? "after" : "before"), $0
      }
    }
    END { printf "checked %d\n", checked }
  ' "$@"
}

# uv_unlocked <file>...: prints "checked <n>", the jobs that run `uv run`, then one line per such job
# whose own `env` does not set UV_LOCKED to 1. A step's env does not count, since it covers that
# step alone. Indentation is read, not assumed: a job is a key one level under `jobs:`, and its
# keys are the next level under it.
uv_unlocked() {
  awk '
    function indent(s) { match(s, /^ */); return RLENGTH }
    function flush() {
      if (job != "" && uses) {
        checked++
        if (!locked) printf "%s: job %s runs uv run without UV_LOCKED: \"1\" in its env\n", file, job
      }
      job = ""; uses = 0; locked = 0; key = -1; in_env = 0
    }
    FNR == 1 { flush(); in_jobs = 0; file = FILENAME }
    /^[ \t]*(#|$)/ { next }
    {
      d = indent($0)
      if (d == 0) { flush(); in_jobs = ($0 ~ /^jobs:/); job_d = -1; next }
      if (!in_jobs) next
      if (job_d < 0) job_d = d
      if (d == job_d) { flush(); job = $1; sub(/:$/, "", job); next }
      if (key < 0) key = d
      if (d == key) in_env = ($1 == "env:")
      else if (d > key && in_env && $0 ~ /^ *UV_LOCKED: *("1"|1) *(#.*)?$/) locked = 1
      if ($0 ~ /(^|[^A-Za-z0-9_-])uv run( |$)/) uses = 1
    }
    END { flush(); printf "checked %d\n", checked }
  ' "$@"
}

# npm_unlocked <file>...: prints one line per npm command that installs other than `npm ci`, then
# "checked <n>", the npm commands that install. `npm install` and its aliases resolve again when
# package-lock.json does not match package.json, where `npm ci` fails. Comment lines are skipped.
npm_unlocked() {
  awk '
    /^[ \t]*#/ { next }
    {
      n = split($0, t, /[ \t]+/)
      for (i = 1; i <= n; i++) {
        if (t[i] !~ /^\(?npm$/) continue
        c = t[i + 1]
        sub(/[;)&|]+$/, "", c)
        if (c == "ci") { checked++; continue }
        if (c ~ /^(install|i|in|add|update|up|upgrade)$/) {
          checked++
          printf "%s:%d: npm %s instead of npm ci: %s\n", FILENAME, FNR, c, $0
        }
      }
    }
    END { printf "checked %d\n", checked }
  ' "$@"
}

# dotnet_unlocked <file>...: prints one line per `dotnet restore` without --locked-mode and per
# `dotnet build` or `dotnet test` without --no-restore, then "checked <n>", the commands of the
# three. Without --locked-mode a restore rewrites a packages.lock.json that does not match the
# projects, and a build or test without --no-restore restores again, unlocked. A command ends at a
# shell separator or a redirection. Comment lines are skipped.
dotnet_unlocked() {
  awk '
    function ends(t) { return t ~ /^(;|&&|\|\||\||&)$/ || t ~ /^[0-9]*[<>]/ }
    /^[ \t]*#/ { next }
    {
      n = split($0, t, /[ \t]+/)
      for (i = 1; i <= n; i++) {
        if (t[i] !~ /^\(?dotnet$/) continue
        c = t[i + 1]
        if (c == "restore") want = "--locked-mode"
        else if (c == "build" || c == "test") want = "--no-restore"
        else continue
        checked++
        found = 0
        for (j = i + 2; j <= n && !ends(t[j]); j++) {
          w = t[j]; sub(/[;)&|]+$/, "", w)
          if (w == want) found = 1
          if (t[j] ~ /[;)&|]$/) break
        }
        if (!found) printf "%s:%d: dotnet %s without %s: %s\n", FILENAME, FNR, c, want, $0
      }
    }
    END { printf "checked %d\n", checked }
  ' "$@"
}

# commands_uv_unlocked <file>...: for a file of commands, one per line, prints one line per command
# running `uv run` without UV_LOCKED=1 set in front of it, then "checked <n>", the commands that run
# `uv run`.
commands_uv_unlocked() {
  awk '
    /(^|[^A-Za-z0-9_-])uv run( |$)/ {
      checked++
      if ($0 !~ /(^|[ \t])UV_LOCKED=1[ \t]+([A-Z_]+=[^ \t]*[ \t]+)*uv run( |$)/) printf "%s:%d: uv run without UV_LOCKED=1 in front: %s\n", FILENAME, FNR, $0
    }
    END { printf "checked %d\n", checked }
  ' "$@"
}

# check <name> <expected output, exact> <command...>
check() {
  local name="$1" want="$2" got
  shift 2
  got="$("$@" 2>&1)"
  if [ "$got" = "$want" ]; then
    echo "ok   $name"; pass=$((pass + 1))
  else
    echo "FAIL $name"; echo "     expected: $want"; echo "     got:      $got"; fail=$((fail + 1))
  fi
}

# The workflows as committed. The counts are what they hold today, so a cargo or uv step that
# moved out of reach of the check, by a rewrite it does not recognise, fails here too.
real=("$workflows"/*.yml)
check "cargo steps pass --locked" "checked 6" cargo_unlocked "${real[@]}"
check "uv jobs set UV_LOCKED" "checked 5" uv_unlocked "${real[@]}"
check "npm installs with npm ci" "checked 3" npm_unlocked "${real[@]}"
# dotnet.yml's restore, build and test, and the same three in libs.yml's C# job.
check "dotnet restores locked, and builds and tests without restoring" "checked 6" dotnet_unlocked "${real[@]}"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# The manifest's commands as committed, one per line. The counts are what it holds today: the
# core's migrate and start, and the brain's migrate and both starts.
jq -r '.services[] | .migrate // empty, .start' "$manifest" > "$tmp/manifest.txt"
check "manifest cargo commands pass --locked" "checked 2" cargo_unlocked "$tmp/manifest.txt"
check "manifest uv commands set UV_LOCKED=1" "checked 3" commands_uv_unlocked "$tmp/manifest.txt"

# Every Dockerfile git tracks. The count is the installs they hold today, each a cargo build, a uv
# sync, an npm ci or a dotnet restore, so one moved out of reach of the check fails here too: the
# core's cargo build, canary's and the brain's uv sync, two npm ci each in the brain's, review's and
# Dev flow's images and one in the proxy's, and review's and Dev flow's dotnet restore.
dockerfiles=()
while IFS= read -r f; do dockerfiles+=("$here/../../$f"); done < <(git -C "$here/../.." ls-files ':(glob)**/Dockerfile')
check "Dockerfiles install locked" "checked 12" bash "$here/dockerfile.sh" unlocked "${dockerfiles[@]}"

# Samples, so each check is seen to fail.

cat > "$tmp/Dockerfile" <<'EOF2'
FROM rust AS build
# RUN cargo build in a comment is not a command
RUN cargo build --release
RUN --mount=type=cache,target=/x \
    cargo build --release --locked && cp a b
RUN uv sync --no-dev
RUN npm install && npm ci
RUN dotnet restore --locked-mode && dotnet publish -c Release
EOF2
check "sample Dockerfile lines" "$tmp/Dockerfile: cargo build without --locked: RUN cargo build --release
$tmp/Dockerfile: uv sync without --locked: RUN uv sync --no-dev
$tmp/Dockerfile: npm installs other than npm ci: RUN npm install 
$tmp/Dockerfile: dotnet publish restores again without --no-restore:  dotnet publish -c Release
checked 5" bash "$here/dockerfile.sh" unlocked "$tmp/Dockerfile"

cat > "$tmp/cargo.yml" <<'EOF'
jobs:
  check:
    steps:
      # cargo test in a comment is not a command
      - run: cargo test
      - run: cargo test --locked --test app_roles -- --ignored
      - run: cargo run -- migrate --locked
      - run: nohup cargo run --locked -q > "$RUNNER_TEMP/core.log" 2>&1 &
      - run: cargo clippy --all-targets -- -D warnings
      - run: cargo sqlx prepare --check -- --all-targets --locked
      - run: cargo sqlx prepare --locked --check -- --all-targets
      - run: cargo fmt --check
      - run: cargo install sqlx-cli --locked
      - run: |
          cargo run --locked -q -- keygen >> .env
          cargo run -q -- keygen --locked >> .env
EOF
check "sample cargo lines" "$tmp/cargo.yml:5: cargo test without --locked before the --:       - run: cargo test
$tmp/cargo.yml:7: cargo run without --locked before the --:       - run: cargo run -- migrate --locked
$tmp/cargo.yml:9: cargo clippy without --locked before the --:       - run: cargo clippy --all-targets -- -D warnings
$tmp/cargo.yml:11: cargo sqlx prepare without --locked after the --:       - run: cargo sqlx prepare --locked --check -- --all-targets
$tmp/cargo.yml:16: cargo run without --locked before the --:           cargo run -q -- keygen --locked >> .env
checked 9" cargo_unlocked "$tmp/cargo.yml"

cat > "$tmp/uv.yml" <<'EOF'
name: Sample

env:
  UV_LOCKED: "1"

jobs:
  unlocked:
    runs-on: ubuntu-latest
    steps:
      - run: uv run pytest
  step_env_only:
    steps:
      - name: Lint
        env:
          UV_LOCKED: "1"
        run: uv run ruff check
  locked:
    env:
      OTHER: x
      UV_LOCKED: "1"
    steps:
      - run: nohup uv run uvicorn app:app &
  set_to_zero:
    env:
      UV_LOCKED: "0"
    steps:
      - run: |
          uv run pytest
  no_uv:
    steps:
      - run: uv sync --locked
EOF
check "sample uv jobs" "$tmp/uv.yml: job unlocked runs uv run without UV_LOCKED: \"1\" in its env
$tmp/uv.yml: job step_env_only runs uv run without UV_LOCKED: \"1\" in its env
$tmp/uv.yml: job set_to_zero runs uv run without UV_LOCKED: \"1\" in its env
checked 4" uv_unlocked "$tmp/uv.yml"

cat > "$tmp/npm.yml" <<'EOF'
jobs:
  check:
    steps:
      # npm install in a comment is not a command
      - run: npm ci
      - run: npm install
      - run: (cd design/ui && npm i)
      - run: npm run build
      - run: npx vitest run
EOF
cat > "$tmp/commands.txt" <<'EOF'
UV_LOCKED=1 uv run brain migrate
uv run uvicorn app:app
UV_LOCKED=0 uv run pytest
FOO=1 UV_LOCKED=1 BAR=2 uv run x
NOT_UV_LOCKED=1 uv run y
cargo run --locked -q
EOF
check "sample manifest uv commands" "$tmp/commands.txt:2: uv run without UV_LOCKED=1 in front: uv run uvicorn app:app
$tmp/commands.txt:3: uv run without UV_LOCKED=1 in front: UV_LOCKED=0 uv run pytest
$tmp/commands.txt:5: uv run without UV_LOCKED=1 in front: NOT_UV_LOCKED=1 uv run y
checked 5" commands_uv_unlocked "$tmp/commands.txt"

check "sample npm lines" "$tmp/npm.yml:6: npm install instead of npm ci:       - run: npm install
$tmp/npm.yml:7: npm i instead of npm ci:       - run: (cd design/ui && npm i)
checked 3" npm_unlocked "$tmp/npm.yml"

cat > "$tmp/dotnet.yml" <<'EOF'
jobs:
  check:
    steps:
      # dotnet restore in a comment is not a command
      - run: dotnet restore --locked-mode
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet build -c Release
      - run: dotnet test --no-build --no-restore -c Release
      - run: dotnet test --no-build -c Release
      - run: (cd x && dotnet restore --locked-mode && dotnet build -c Release)
      - run: dotnet restore > log --locked-mode
      - run: dotnet format --verify-no-changes --no-restore
      - run: dotnet tool restore
EOF
check "sample dotnet lines" "$tmp/dotnet.yml:6: dotnet restore without --locked-mode:       - run: dotnet restore
$tmp/dotnet.yml:8: dotnet build without --no-restore:       - run: dotnet build -c Release
$tmp/dotnet.yml:10: dotnet test without --no-restore:       - run: dotnet test --no-build -c Release
$tmp/dotnet.yml:11: dotnet build without --no-restore:       - run: (cd x && dotnet restore --locked-mode && dotnet build -c Release)
$tmp/dotnet.yml:12: dotnet restore without --locked-mode:       - run: dotnet restore > log --locked-mode
checked 9" dotnet_unlocked "$tmp/dotnet.yml"

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
