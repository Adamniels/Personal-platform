#!/usr/bin/env bash
# Offline tests for reset-db.sh, with a fake `docker` on PATH that records its arguments and the SQL
# it is fed, and exits with $FAKE_DOCKER_EXIT.
# Run: bash scripts/agent/reset-db.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
pass=0
fail=0

ok() { echo "ok   $1"; pass=$((pass + 1)); }
bad() { echo "FAIL $1"; [ -n "${2:-}" ] && echo "     $2"; fail=$((fail + 1)); }

mkdir -p "$tmp/bin"
cat > "$tmp/bin/docker" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" > "$FAKE_DIR/args"
pwd > "$FAKE_DIR/cwd"
cat > "$FAKE_DIR/sql"
exit "${FAKE_DOCKER_EXIT:-0}"
EOF
chmod +x "$tmp/bin/docker"

run() {
  rm -f "$tmp/args" "$tmp/sql" "$tmp/cwd"
  (cd /tmp && FAKE_DIR="$tmp" PATH="$tmp/bin:$PATH" bash "$here/reset-db.sh" "$@" > "$tmp/out" 2>&1)
}

# A successful reset.
FAKE_DOCKER_EXIT=0 run
rc=$?
if [ "$rc" -eq 0 ]; then ok "exits 0 when docker succeeds"; else bad "exits 0 when docker succeeds" "rc=$rc: $(cat "$tmp/out")"; fi

args="$(cat "$tmp/args" 2>/dev/null)"
want_args="compose --project-directory infra exec -T postgres psql -U postgres -d postgres -v ON_ERROR_STOP=1"
if [ "$args" = "$want_args" ]; then ok "runs psql in the postgres service over the socket"; else bad "runs psql in the postgres service over the socket" "got: $args"; fi

root="$(cd "$here/../.." && pwd -P)"
cwd="$(cat "$tmp/cwd" 2>/dev/null)"
if [ "$cwd" = "$root" ]; then ok "runs from the repository root, wherever it is called from"; else bad "runs from the repository root" "got: $cwd"; fi

sql="$(grep -v '^[[:space:]]*\(--.*\)\?$' "$tmp/sql" 2>/dev/null)"
want_sql='DROP DATABASE IF EXISTS core WITH (FORCE);
CREATE DATABASE core OWNER core_owner;
REVOKE ALL ON DATABASE core FROM PUBLIC;
GRANT CONNECT ON DATABASE core TO core_app;'
if [ "$sql" = "$want_sql" ]; then ok "feeds exactly the reset statements, in order"; else bad "feeds exactly the reset statements, in order" "got: $(echo "$sql" | tr '\n' ' ')"; fi

if grep -qi 'role' "$tmp/sql"; then bad "keeps the roles" "the SQL touches a role"; else ok "keeps the roles"; fi

# A failing docker, such as the daemon down or psql failing.
FAKE_DOCKER_EXIT=3 run
rc=$?
if [ "$rc" -eq 1 ]; then ok "exits 1 when docker fails"; else bad "exits 1 when docker fails" "rc=$rc"; fi
if grep -q 'database reset failed' "$tmp/out"; then ok "says why on failure"; else bad "says why on failure" "$(cat "$tmp/out")"; fi

# Docker missing exits 127 in the shell; a real PATH without docker cannot be built portably, since
# CI runners keep docker in /usr/bin, so the fake stands in for it.
FAKE_DOCKER_EXIT=127 run
rc=$?
if [ "$rc" -eq 1 ]; then ok "exits 1 when docker is missing"; else bad "exits 1 when docker is missing" "rc=$rc"; fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
