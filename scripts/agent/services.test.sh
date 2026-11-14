#!/usr/bin/env bash
# Offline tests for services.sh, in a fake repository holding a copy of it, a manifest of three
# services and an infra/.env. Each service's commands are a recorder that writes the directory it
# ran in and its environment to a file, so the tests see what the command was given without a
# real service, and can check that services.sh itself never prints a password.
# Run: bash scripts/agent/services.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
pass=0
fail=0

ok() { echo "ok   $1"; pass=$((pass + 1)); }
bad() { echo "FAIL $1"; [ -n "${2:-}" ] && echo "     $2"; fail=$((fail + 1)); }
same() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "expected: $(printf '%s' "$3" | tr '\n' '|')  got: $(printf '%s' "$2" | tr '\n' '|')"; fi; }

repo="$tmp/repo"
mkdir -p "$repo/scripts/agent" "$repo/infra" "$repo/Alpha" "$repo/beta" "$repo/Gamma/svc"
cp "$here/services.sh" "$repo/scripts/agent/services.sh"

# The recorder: the step it was asked for, then its directory, then its environment.
cat > "$tmp/record" <<'EOF'
#!/usr/bin/env bash
{ echo "step=$1"; echo "cwd=$(pwd -P)"; env; } > "$RECORD_TO"
exit "${RECORD_EXIT:-0}"
EOF
chmod +x "$tmp/record"

cat > "$repo/infra/services.json" <<EOF
{
  "services": [
    {
      "name": "alpha", "dir": "Alpha", "port": 9001, "health": "/login", "language": "rust",
      "area": "core",
      "migrate": "$tmp/record migrate",
      "start": "$tmp/record start",
      "database": {
        "name": "alpha", "owner": "alpha_owner", "app": "alpha_app",
        "owner_password": "ALPHA_OWNER_PASSWORD", "app_password": "ALPHA_APP_PASSWORD",
        "owner_url": "ALPHA_OWNER_DATABASE_URL", "app_url": "ALPHA_APP_DATABASE_URL"
      }
    },
    {
      "name": "beta", "dir": "beta", "port": 9002, "language": "python", "area": "canary",
      "start": "$tmp/record start"
    },
    {
      "name": "gamma", "dir": "Gamma/svc", "port": 9003, "health": "/gamma/api/health",
      "language": "python", "area": "brain",
      "migrate": "$tmp/record migrate",
      "start": "$tmp/record start",
      "database": {
        "name": "gamma", "owner": "gamma_owner", "app": "gamma_app",
        "owner_password": "GAMMA_OWNER_PASSWORD", "app_password": "GAMMA_APP_PASSWORD",
        "owner_url": "GAMMA_OWNER_DATABASE_URL"
      }
    }
  ]
}
EOF

# Passwords that would be mangled in a URL unless encoded, and that are easy to spot in output.
cat > "$repo/infra/.env" <<'EOF'
POSTGRES_PASSWORD=never-read
ALPHA_OWNER_PASSWORD=owner-s3cret@x/y
ALPHA_APP_PASSWORD=app-s3cret
GAMMA_OWNER_PASSWORD=gamma-owner-s3cret
GAMMA_APP_PASSWORD=gamma-app-s3cret
EOF

services="$repo/scripts/agent/services.sh"
all_output="$tmp/all-output"
: > "$all_output"

# svc <args>: runs services.sh from an unrelated directory, output in $out, exit code in $rc, the
# recorder's file in $rec (empty when it did not run).
svc() {
  rm -f "$tmp/rec"
  out="$(cd / && RECORD_TO="$tmp/rec" bash "$services" "$@" 2>&1)"
  rc=$?
  printf '%s\n' "$out" >> "$all_output"
  rec="$(cat "$tmp/rec" 2>/dev/null)"
}
# var <name>: the value the recorder saw for <name>, or "(unset)".
var() { printf '%s\n' "$rec" | grep -q "^$1=" && printf '%s\n' "$rec" | grep "^$1=" | head -n1 | cut -d= -f2- || echo "(unset)"; }

# The lists.
svc names
same "names prints the names in manifest order" "$rc $out" "0 alpha
beta
gamma"

svc ports
same "ports prints each port in manifest order" "$rc $out" "0 9001
9002
9003"

svc databases
same "databases prints name, owner and app role of each entry with one" "$rc $out" "0 alpha alpha_owner alpha_app
gamma gamma_owner gamma_app"

svc password-vars
same "password-vars prints every password variable" "$rc $out" "0 ALPHA_OWNER_PASSWORD
ALPHA_APP_PASSWORD
GAMMA_OWNER_PASSWORD
GAMMA_APP_PASSWORD"

svc health
same "health prints each health path, skipping a service without one" "$rc $out" "0 /login
/gamma/api/health"

svc areas
same "areas prints each folder and its area" "$rc $out" "0 Alpha core
beta canary
Gamma/svc brain"

svc dotnet-services
same "dotnet-services prints nothing without a C# service" "$rc $out" "0 "

# The same manifest with gamma written in C#, given a CI init script, in a copy of the repository.
cp -R "$repo" "$tmp/dotnet-repo"
jq '.services[2].language = "dotnet" | .services[2].area = "dotnet"
  | .services[2].ci = {"init_script": "infra/postgres/initdb/gamma.sh"}' \
  "$repo/infra/services.json" > "$tmp/dotnet-repo/infra/services.json"
out="$(bash "$tmp/dotnet-repo/scripts/agent/services.sh" dotnet-services 2>&1)"
same "dotnet-services prints each C# service as one line of the fields dotnet.yml takes" "$out" \
  "{\"name\":\"gamma\",\"dir\":\"Gamma/svc\",\"postgres\":true,\"init_script\":\"infra/postgres/initdb/gamma.sh\",\"migrate\":\"$tmp/record migrate\",\"database\":\"gamma\",\"owner\":\"gamma_owner\",\"app\":\"gamma_app\",\"owner_url\":\"GAMMA_OWNER_DATABASE_URL\",\"app_url\":\"\",\"owner_password\":\"GAMMA_OWNER_PASSWORD\",\"app_password\":\"GAMMA_APP_PASSWORD\"}"

# run <name> migrate.
svc run alpha migrate
same "migrate exits with the command's code" "$rc" "0"
same "migrate runs the entry's migrate command" "$(var step)" "migrate"
same "migrate runs from the entry's folder" "$(var cwd)" "$(cd "$repo/Alpha" && pwd -P)"
same "migrate sets the owner's URL, the password encoded" "$(var ALPHA_OWNER_DATABASE_URL)" \
  "postgres://alpha_owner:owner-s3cret%40x%2Fy@127.0.0.1:5432/alpha"
same "migrate does not set the app's URL" "$(var ALPHA_APP_DATABASE_URL)" "(unset)"

svc run gamma migrate
same "migrate works for an entry in a nested folder" "$(var cwd)" "$(cd "$repo/Gamma/svc" && pwd -P)"
same "migrate sets that entry's own owner URL" "$(var GAMMA_OWNER_DATABASE_URL)" \
  "postgres://gamma_owner:gamma-owner-s3cret@127.0.0.1:5432/gamma"

svc run beta migrate
same "an entry without migrate makes migrate a no op that exits 0" "$rc ${rec:-(not run)}" "0 (not run)"

RECORD_EXIT=7 svc run alpha migrate
same "a failing migrate fails run with its code" "$rc" "7"

# run <name> start.
svc run alpha start
same "start runs the entry's start command" "$(var step)" "start"
same "start runs from the entry's folder" "$(var cwd)" "$(cd "$repo/Alpha" && pwd -P)"
same "start sets the app's URL" "$(var ALPHA_APP_DATABASE_URL)" "postgres://alpha_app:app-s3cret@127.0.0.1:5432/alpha"
same "start sets HOST to 127.0.0.1 by default" "$(var HOST)" "127.0.0.1"
same "start sets PORT to the entry's port" "$(var PORT)" "9001"
same "start never gives the service its owner's URL" "$(var ALPHA_OWNER_DATABASE_URL)" "(unset)"

rm -f "$tmp/rec"
out="$(cd / && HOST=0.0.0.0 ALPHA_OWNER_DATABASE_URL=leaked RECORD_TO="$tmp/rec" bash "$services" run alpha start 2>&1)"
printf '%s\n' "$out" >> "$all_output"
rec="$(cat "$tmp/rec" 2>/dev/null)"
same "start keeps a HOST the caller sets" "$(var HOST)" "0.0.0.0"
same "start drops an owner URL the caller had set" "$(var ALPHA_OWNER_DATABASE_URL)" "(unset)"

svc run gamma start
same "start without app_url sets no app URL" "$(var GAMMA_APP_DATABASE_URL)" "(unset)"

svc run beta start
same "start works for an entry without a database" "$rc $(var step) $(var PORT)" "0 start 9002"

# Errors.
svc run nope start
same "an unknown name exits 2" "$rc" "2"
case "$out" in *nope*) ok "an unknown name is named" ;; *) bad "an unknown name is named" "$out" ;; esac

svc run alpha restart
same "an unknown step exits 2" "$rc" "2"

svc bogus
same "an unknown subcommand exits 2" "$rc" "2"

svc
same "no subcommand exits 2" "$rc" "2"

grep -v '^ALPHA_OWNER_PASSWORD=' "$repo/infra/.env" > "$tmp/env" && cp "$tmp/env" "$repo/infra/.env"
svc run alpha migrate
same "a password missing from infra/.env exits 1 without running the command" "$rc ${rec:-(not run)}" "1 (not run)"
case "$out" in *ALPHA_OWNER_PASSWORD*) ok "says which variable is missing" ;; *) bad "says which variable is missing" "$out" ;; esac

rm -f "$repo/infra/.env"
svc run alpha start
same "a missing infra/.env exits 1" "$rc ${rec:-(not run)}" "1 (not run)"

echo 'not json' > "$repo/infra/services.json"
svc names
same "a manifest that does not parse exits 1" "$rc" "1"

# Nothing services.sh printed, in any case above, holds a password.
if grep -qE 's3cret|never-read' "$all_output"; then
  bad "never prints a password" "$(grep -E 's3cret|never-read' "$all_output" | head -n 3)"
else
  ok "never prints a password"
fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
