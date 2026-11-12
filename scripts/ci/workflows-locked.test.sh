#!/usr/bin/env bash
# Checks that CI tests the committed lockfiles: every cargo command in a workflow that resolves
# dependencies passes --locked, and every job that runs `uv run` sets UV_LOCKED to 1. Without them
# cargo and uv resolve again when a lockfile does not match its manifest, and the push goes green.
# The same checks run on sample workflows, so they cannot pass by matching nothing.
# Run: bash scripts/ci/workflows-locked.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
workflows="$here/../../.github/workflows"
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
check "cargo steps pass --locked" "checked 8" cargo_unlocked "${real[@]}"
check "uv jobs set UV_LOCKED" "checked 2" uv_unlocked "${real[@]}"

# Sample workflows, so each check is seen to fail.
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

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

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
