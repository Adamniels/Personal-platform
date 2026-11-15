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
printf '%s\n' "$*" >> "$FAKE_DIR/calls"
pwd > "$FAKE_DIR/cwd"
cat > "$FAKE_DIR/sql"
exit "${FAKE_DOCKER_EXIT:-0}"
EOF
chmod +x "$tmp/bin/docker"

run() {
  rm -f "$tmp/args" "$tmp/sql" "$tmp/cwd" "$tmp/calls"
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
# The real manifest's databases, read here with jq rather than through services.sh, so a service
# that gains a database needs no change to this test. The exact statements and their order are
# pinned below, against a manifest of the test's own.
want_sql="$(jq -r '.services[] | .database // empty
  | "DROP DATABASE IF EXISTS \(.name) WITH (FORCE);",
    "CREATE DATABASE \(.name) OWNER \(.owner);",
    "REVOKE ALL ON DATABASE \(.name) FROM PUBLIC;",
    "GRANT CONNECT ON DATABASE \(.name) TO \(.app);"' "$root/infra/services.json")"
if [ -n "$want_sql" ] && [ "$sql" = "$want_sql" ]; then ok "feeds exactly the reset statements for every database in the manifest, in order"; else bad "feeds exactly the reset statements for every database in the manifest, in order" "got: $(echo "$sql" | tr '\n' ' ')"; fi

# the_reset_only_ever_addresses_the_dev_project: every docker call is the dev compose project's,
# found by --project-directory infra, and none names the hosted copy, whose databases are real.
calls="$(cat "$tmp/calls" 2>/dev/null)"
if [ -n "$calls" ] && ! printf '%s\n' "$calls" | grep -qv '^compose --project-directory infra ' &&
  ! printf '%s\n' "$calls" | grep -q 'hosted'; then
  ok the_reset_only_ever_addresses_the_dev_project
else
  bad the_reset_only_ever_addresses_the_dev_project "calls: $(printf '%s\n' "$calls" | tr '\n' ';')"
fi

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

# The databases come from the manifest, through services.sh: a fake repository with a copy of both
# scripts and a manifest of its own.
fake="$tmp/repo"
mkdir -p "$fake/scripts/agent" "$fake/infra"
cp "$here/reset-db.sh" "$here/services.sh" "$fake/scripts/agent/"
db() { printf '"database": {"name": "%s", "owner": "%s", "app": "%s", "owner_password": "X_OWNER_PASSWORD", "app_password": "X_APP_PASSWORD", "owner_url": "X_OWNER_DATABASE_URL"}' "$1" "$2" "$3"; }
manifest() {
  cat > "$fake/infra/services.json" <<EOF
{"services": [
  {"name": "zeta", "dir": "zeta", "port": 1, "language": "rust", "area": "core", "start": "true", "migrate": "true", $(db zeta zeta_owner zeta_app)},
  {"name": "plain", "dir": "plain", "port": 2, "language": "python", "area": "canary", "start": "true"},
  {"name": "alpha", "dir": "alpha", "port": 3, "language": "python", "area": "brain", "start": "true", "migrate": "true", $(db "$1" "$2" alpha_app)}
]}
EOF
}
fake_run() {
  rm -f "$tmp/args" "$tmp/sql" "$tmp/cwd"
  (cd /tmp && FAKE_DIR="$tmp" PATH="$tmp/bin:$PATH" bash "$fake/scripts/agent/reset-db.sh" > "$tmp/out" 2>&1)
}

manifest alpha alpha_owner
FAKE_DOCKER_EXIT=0 fake_run
rc=$?
sql="$(grep -v '^[[:space:]]*\(--.*\)\?$' "$tmp/sql" 2>/dev/null)"
want_sql='DROP DATABASE IF EXISTS zeta WITH (FORCE);
CREATE DATABASE zeta OWNER zeta_owner;
REVOKE ALL ON DATABASE zeta FROM PUBLIC;
GRANT CONNECT ON DATABASE zeta TO zeta_app;
DROP DATABASE IF EXISTS alpha WITH (FORCE);
CREATE DATABASE alpha OWNER alpha_owner;
REVOKE ALL ON DATABASE alpha FROM PUBLIC;
GRANT CONNECT ON DATABASE alpha TO alpha_app;'
if [ "$rc" -eq 0 ] && [ "$sql" = "$want_sql" ]; then ok "resets each database in the manifest, in manifest order, skipping a service without one"; else bad "resets each database in the manifest, in manifest order, skipping a service without one" "rc=$rc got: $(echo "$sql" | tr '\n' ' ')"; fi

# A name that is not a plain identifier never reaches psql.
manifest 'alpha; DROP DATABASE zeta' alpha_owner
FAKE_DOCKER_EXIT=0 fake_run
rc=$?
if [ "$rc" -eq 1 ] && [ ! -e "$tmp/sql" ]; then ok "refuses a database name that is not an identifier, before docker runs"; else bad "refuses a database name that is not an identifier, before docker runs" "rc=$rc, sql: $(cat "$tmp/sql" 2>/dev/null | tr '\n' ' ')"; fi

manifest alpha 'Alpha-Owner'
FAKE_DOCKER_EXIT=0 fake_run
rc=$?
if [ "$rc" -eq 1 ] && [ ! -e "$tmp/sql" ]; then ok "refuses a role that is not an identifier, before docker runs"; else bad "refuses a role that is not an identifier, before docker runs" "rc=$rc"; fi

echo 'not json' > "$fake/infra/services.json"
FAKE_DOCKER_EXIT=0 fake_run
rc=$?
if [ "$rc" -eq 1 ] && [ ! -e "$tmp/sql" ] && grep -q 'database reset failed' "$tmp/out"; then ok "fails, before docker runs, when the manifest cannot be read"; else bad "fails, before docker runs, when the manifest cannot be read" "rc=$rc: $(cat "$tmp/out")"; fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
