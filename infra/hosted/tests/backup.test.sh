#!/usr/bin/env bash
# The backup job's acceptance tests, against a running throwaway copy: dumping, encrypting,
# uploading to the stand in bucket, the status file, local retention, restoring, the schedule and
# the backup role. See "Backups" in infra/hosted/README.md.
#
# check.sh runs it once its project is up, with the backup service and s3stub running:
#   HOSTED_PROJECT=<project> HOSTED_ENV_FILE=<its env file> bash infra/hosted/tests/backup.test.sh
# It refuses platform-hosted, the real copy, before running any docker command: it creates and
# drops databases and runs restores.
#
# It makes its own age key pair inside a container, keeps the private key in its temporary folder
# for the run, and removes it at exit. It creates the database probe and its roles, with a table
# under forced row level security, and restores each database it backs up under <name>_restored.
#
# Each case prints `ok <name>` or `FAIL <name>`, and the script exits non zero if any failed.
# Written for bash 3.2, the one macOS ships.
set -uo pipefail

project="${HOSTED_PROJECT:-}"
env_file="${HOSTED_ENV_FILE:-}"
if [ -z "$project" ] || [ -z "$env_file" ] || [ ! -f "$env_file" ]; then
  echo "backup.test.sh: set HOSTED_PROJECT and HOSTED_ENV_FILE, see check.sh" >&2
  exit 2
fi
if [ "$project" = platform-hosted ]; then
  echo "backup.test.sh: refusing to run as platform-hosted, the real copy's project" >&2
  exit 2
fi

here="$(cd "$(dirname "$0")" && pwd)"
hosted="$(cd "$here/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
pass=0
fail=0

ok() { echo "ok   $1"; pass=$((pass + 1)); }
bad() { echo "FAIL $1"; [ -n "${2:-}" ] && printf '     %s\n' "$2"; fail=$((fail + 1)); }

compose() {
  docker compose -p "$project" --env-file "$env_file" \
    -f "$hosted/compose.yaml" -f "$hosted/front/compose.yaml" -f "$hosted/tests/compose.yaml" "$@"
}

# setting <name>: its value in the env file.
setting() { sed -n "s/^$1=//p" "$env_file" | tail -n 1; }

# sql <database>: the statements on stdin, as the superuser over the container's socket, unaligned.
sql() { compose exec -T postgres psql -U postgres -d "$1" -v ON_ERROR_STOP=1 -AtX -q; }

# as_role <role> <password> <database>: the statements on stdin as that role, over TCP, so its
# password and its CONNECT are what let it in.
as_role() {
  PGPASSWORD="$2" compose exec -T -e PGPASSWORD postgres \
    psql -h 127.0.0.1 -U "$1" -d "$3" -v ON_ERROR_STOP=1 -AtX -q
}

# in_backup <script>: bash in a one off container of the backup image, with the volumes. Its
# output is the script's alone: compose's own lines on stderr go to a log.
in_backup() { compose run --rm -T --no-deps --entrypoint bash backup -c "$1" 2>> "$tmp/compose.log"; }

# backup_now [<docker compose run option>...]: `backup now` with this run's recipient.
backup_now() {
  compose run --rm -T --no-deps -e "BACKUP_AGE_RECIPIENT=$recipient" "$@" backup now
}

# files: the dump files in the volume, one name per line, sorted.
files() { in_backup 'cd /backups && ls -1 -- *.dump.age 2>/dev/null | sort'; }

# new_files <before> <after>: the names in the second list and not the first.
new_files() { comm -13 <(printf '%s\n' "$1" | grep .) <(printf '%s\n' "$2" | grep .); }

status() { in_backup 'cat /status/backup.json'; }

# databases: what a backup must hold, asked of Postgres the way the job asks it.
databases() {
  echo "SELECT datname FROM pg_database WHERE NOT datistemplate AND datname <> 'postgres' ORDER BY 1" |
    sql postgres
}

# fingerprint <database>: everything a restore must bring back, as the superuser: the database's
# owner and grants; each schema's owner and grants; each relation's kind, owner, grants and row
# level security; each policy; and each table's row count and a hash of its rows in primary key
# order, or in the order of the row's text where it has no primary key.
fingerprint() {
  sql "$1" << SQL
SELECT 'database', pg_get_userbyid(d.datdba),
  (SELECT string_agg(format('%s %s %s', a.grantee::regrole, a.privilege_type, a.is_grantable), ', '
     ORDER BY a.grantee::regrole::text, a.privilege_type)
   FROM aclexplode(d.datacl) a)
FROM pg_database d WHERE d.datname = '$1';
SELECT 'schema', n.nspname, pg_get_userbyid(n.nspowner), coalesce(n.nspacl::text, '')
FROM pg_namespace n
WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_toast%'
  AND n.nspname NOT LIKE 'pg\_temp%'
ORDER BY 2;
SELECT 'relation', n.nspname, c.relname, c.relkind, pg_get_userbyid(c.relowner),
  coalesce(c.relacl::text, ''), c.relrowsecurity, c.relforcerowsecurity
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_toast%'
  AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
ORDER BY 2, 3;
SELECT 'policy', schemaname, tablename, policyname, permissive, roles::text, cmd,
  coalesce(qual, ''), coalesce(with_check, '')
FROM pg_policies ORDER BY 2, 3, 4;
SELECT format(
  'SELECT %L, count(*), md5(coalesce(string_agg(t::text, E''\\n'' ORDER BY %s), '''')) FROM %I.%I t',
  n.nspname || '.' || c.relname,
  coalesce(
    (SELECT string_agg(format('t.%I', a.attname), ', ' ORDER BY k.ord)
     FROM pg_index i
     CROSS JOIN unnest(i.indkey) WITH ORDINALITY k(attnum, ord)
     JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
     WHERE i.indrelid = c.oid AND i.indisprimary),
    't::text'),
  n.nspname, c.relname)
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_toast%'
  AND c.relkind IN ('r', 'p')
ORDER BY n.nspname, c.relname
\gexec
SQL
}

# --- Setup: a key pair, the probe database, and the backup role run again. -----------------------

echo "-- setup"
# The private key comes out on stdout, the recipient is derived from it; neither leaves $tmp.
if ! in_backup 'age-keygen 2> /dev/null' > "$tmp/identity" ||
  ! recipient="$(in_backup 'age-keygen -y' < "$tmp/identity")" ||
  [ "${recipient#age1}" = "$recipient" ]; then
  echo "backup.test.sh: could not make an age key pair in the backup image" >&2
  exit 1
fi
db_password="$(setting BACKUP_DB_PASSWORD)"
s3_key="$(setting BACKUP_S3_KEY)"
super_password="$(setting POSTGRES_PASSWORD)"
probe_password="$(openssl rand -hex 16)"
bucket_prefix="/$(setting BACKUP_S3_BUCKET)/platform"

# probe: an owner, an app role that logs in, and a table under forced row level security holding
# rows of two users, made as the superuser, with the database's grants in the style of the init
# scripts. Dropped first, with every copy, so the script can run again on the same project.
{
  for database in $(echo "SELECT datname FROM pg_database WHERE datname LIKE '%\_restored'" | sql postgres); do
    echo "DROP DATABASE $database WITH (FORCE);"
  done
  cat << SQL
DROP DATABASE IF EXISTS probe WITH (FORCE);
DROP ROLE IF EXISTS probe_app;
DROP ROLE IF EXISTS probe_owner;
CREATE ROLE probe_owner;
CREATE ROLE probe_app LOGIN PASSWORD '$probe_password';
CREATE DATABASE probe OWNER probe_owner;
REVOKE ALL ON DATABASE probe FROM PUBLIC;
GRANT CONNECT ON DATABASE probe TO probe_app;
SQL
} | sql postgres > "$tmp/probe.log" 2>&1 &&
  sql probe << 'SQL' >> "$tmp/probe.log" 2>&1
CREATE TABLE scoped (id integer PRIMARY KEY, user_id integer NOT NULL, note text NOT NULL);
ALTER TABLE scoped OWNER TO probe_owner;
ALTER TABLE scoped ENABLE ROW LEVEL SECURITY;
ALTER TABLE scoped FORCE ROW LEVEL SECURITY;
CREATE POLICY per_user ON scoped USING (user_id = current_setting('app.user_id')::integer);
GRANT SELECT, INSERT ON scoped TO probe_app;
INSERT INTO scoped VALUES (1, 1, 'the first user''s row'), (2, 2, 'the second user''s row');
SQL
[ $? -eq 0 ] || { echo "backup.test.sh: could not create probe:" >&2; cat "$tmp/probe.log" >&2; exit 1; }

# A database made after the volume was: the role's script grants CONNECT on it when run again.
if ! compose exec -T postgres bash /docker-entrypoint-initdb.d/zz-backup-role.sh > "$tmp/role.log" 2>&1; then
  echo "backup.test.sh: backup-role.sh failed:" >&2
  cat "$tmp/role.log" >&2
  exit 1
fi

# --- the_backup_role_can_read_and_cannot_write ----------------------------------------------------

got="$(echo 'SELECT count(*) FROM scoped' | as_role platform_backup "$db_password" probe 2>&1)"
refused="$(echo "INSERT INTO scoped VALUES (3, 1, 'written by the backup role')" |
  as_role platform_backup "$db_password" probe 2>&1)"
code=$?
attributes="$(echo "SELECT rolbypassrls, rolsuper, rolcreatedb, rolcreaterole, pg_has_role('platform_backup', 'pg_read_all_data', 'MEMBER') FROM pg_roles WHERE rolname = 'platform_backup'" | sql postgres)"
if [ "$got" = 2 ] && [ "$code" -ne 0 ] && printf '%s' "$refused" | grep -q 'permission denied' &&
  [ "$attributes" = "t|f|f|f|t" ]; then
  ok the_backup_role_can_read_and_cannot_write
else
  bad the_backup_role_can_read_and_cannot_write "select: $got; insert exit $code: $refused; bypassrls|super|createdb|createrole|pg_read_all_data: $attributes"
fi

# --- a_missing_setting_stops_the_job_naming_it ----------------------------------------------------

before="$(files)"
problems=""
for var in BACKUP_DB_PASSWORD BACKUP_AGE_RECIPIENT BACKUP_S3_ENDPOINT BACKUP_S3_REGION \
  BACKUP_S3_BUCKET BACKUP_S3_KEY_ID BACKUP_S3_KEY; do
  out="$(compose run --rm -T --no-deps -e "$var=" backup now 2>&1)"
  code=$?
  if [ "$code" -eq 0 ] || ! printf '%s' "$out" | grep -q "$var"; then
    problems="$problems $var unset: exit $code, $(printf '%s' "$out" | tail -n 1);"
  fi
done
# Malformed: a recipient that is not one, and the three settings with a format.
for pair in "BACKUP_AGE_RECIPIENT=AGE-SECRET-KEY-1QQQQ" "BACKUP_AT=25:00" \
  "BACKUP_KEEP_LOCAL_DAYS=seven" "TZ=Europe/Nowhere"; do
  var="${pair%%=*}"
  out="$(compose run --rm -T --no-deps -e "$pair" backup now 2>&1)"
  code=$?
  if [ "$code" -eq 0 ] || ! printf '%s' "$out" | grep -q "$var"; then
    problems="$problems $pair: exit $code, $(printf '%s' "$out" | tail -n 1);"
  fi
done
after="$(files)"
if [ -z "$problems" ] && [ "$before" = "$after" ]; then
  ok a_missing_setting_stops_the_job_naming_it
else
  bad a_missing_setting_stops_the_job_naming_it "${problems:-} files before: $(echo $before), after: $(echo $after)"
fi

# --- The first backup, so the second replaces a status file that exists. -------------------------

first_out="$(backup_now 2>&1)"
first_code=$?
first_inode="$(in_backup 'stat -c %i /status/backup.json' 2>&1)"

# --- The second backup, through shims recording the arguments of each program it runs. ----------

mkdir -p "$tmp/shim"
for program in curl pg_dump psql age; do
  cat > "$tmp/shim/$program" << 'EOF'
#!/bin/bash
# Records its arguments, then runs the real program of the same name.
printf '%s %s\n' "${0##*/}" "$*" >> /tmp/argv.log
PATH="${PATH#/shim:}" exec "${0##*/}" "$@"
EOF
  chmod 755 "$tmp/shim/$program"
done
before="$(files)"
compose run --rm -T --no-deps -e "BACKUP_AGE_RECIPIENT=$recipient" \
  -v "$tmp/shim:/shim:ro" --entrypoint bash backup -c '
    PATH="/shim:$PATH" /usr/local/lib/backup/main.sh now > /tmp/out.log 2>&1
    code=$?
    cat /tmp/out.log > /backups/.test-out.log
    cat /tmp/argv.log > /backups/.test-argv.log 2> /dev/null
    exit $code' > /dev/null 2>&1
second_code=$?
in_backup 'cat /backups/.test-out.log; rm -f /backups/.test-out.log' > "$tmp/second.out" 2>&1
in_backup 'cat /backups/.test-argv.log; rm -f /backups/.test-argv.log' > "$tmp/argv.log" 2>&1
after="$(files)"
run_files="$(new_files "$before" "$after")"
status > "$tmp/status.json" 2>&1
second_inode="$(in_backup 'stat -c %i /status/backup.json' 2>&1)"
status_leftovers="$(in_backup 'ls -A /status | grep -vx backup.json' 2>&1)"

# --- a_backup_dumps_every_database_encrypted ------------------------------------------------------

expected="$(databases)"
stamps="$(printf '%s\n' "$run_files" | grep . | sed 's/-.*//' | sort -u)"
names="$(printf '%s\n' "$run_files" | grep . | sed -E 's/^[0-9]{8}T[0-9]{6}Z-(.*)\.dump\.age$/\1/' | sort)"
heads="$(in_backup "cd /backups && for f in $(echo $run_files); do
  printf '%s %s %s\n' \"\$f\" \"\$(stat -c %s \"\$f\")\" \"\$(head -c 21 \"\$f\")\"
done" 2>&1)"
problems=""
[ "$first_code" -eq 0 ] || problems="$problems the first backup exited $first_code;"
[ "$second_code" -eq 0 ] || problems="$problems the second backup exited $second_code: $(tail -n 3 "$tmp/second.out" | tr '\n' ' ');"
[ "$(printf '%s\n' "$stamps" | grep -c .)" = 1 ] || problems="$problems not one time stamp: $(echo $stamps);"
[ "$names" = "$expected" ] || problems="$problems databases $(echo $names), expected $(echo $expected);"
while read -r name size head; do
  [ -z "$name" ] && continue
  [ "$size" -gt 0 ] 2> /dev/null || problems="$problems $name is empty;"
  case "$head" in
    PGDMP*) problems="$problems $name begins with PGDMP;" ;;
    age-encryption.org/v1) ;;
    *) problems="$problems $name begins with '$head';" ;;
  esac
done <<< "$heads"
if [ -z "$problems" ] && [ -n "$names" ]; then
  ok a_backup_dumps_every_database_encrypted
else
  bad a_backup_dumps_every_database_encrypted "$problems"
fi

# --- every_dump_reaches_the_bucket ---------------------------------------------------------------

local_sums="$(in_backup "cd /backups && sha256sum $(echo $run_files)" 2>&1 | awk '{ print $2, $1 }' | sort)"
bucket_sums="$(compose exec -T s3stub sh -c "cd /tmp/s3/objects$bucket_prefix && sha256sum $(echo $run_files)" 2>&1 |
  awk '{ print $2, $1 }' | sort)"
requests="$(compose exec -T s3stub cat /tmp/s3/requests.jsonl 2>&1)"
problems=""
[ -n "$local_sums" ] && [ "$local_sums" = "$bucket_sums" ] ||
  problems="$problems local: $(echo $local_sums); bucket: $(echo $bucket_sums);"
for name in $run_files; do
  line="$(printf '%s\n' "$requests" | jq -c --arg p "$bucket_prefix/$name" 'select(.path == $p and .status == 200)' 2> /dev/null | tail -n 1)"
  if [ -z "$line" ]; then
    problems="$problems no stored request for $name;"
  elif [ "$(printf '%s' "$line" | jq -r '"\(.auth_scheme) \(.md5_ok)"')" != "AWS4-HMAC-SHA256 true" ]; then
    problems="$problems $name: $line;"
  fi
done
# The stub itself refuses an unsigned request and a wrong Content-MD5, so the case above cannot
# pass by a stub that stores anything.
unsigned="$(in_backup 'printf x > /tmp/x; curl -s -o /dev/null -w %{http_code} -T /tmp/x -H "Content-MD5: LZRhbDqmK6NNDuvMwGdiOw==" http://s3stub:9000/b/unsigned' 2>&1)"
wrong_md5="$(in_backup 'printf x > /tmp/x; curl -s -o /dev/null -w %{http_code} -T /tmp/x -H "Authorization: AWS4-HMAC-SHA256 Credential=x" -H "Content-MD5: AAAAAAAAAAAAAAAAAAAAAA==" http://s3stub:9000/b/wrong' 2>&1)"
[ "$unsigned $wrong_md5" = "403 403" ] || problems="$problems the stub answered $unsigned unsigned and $wrong_md5 with a wrong Content-MD5;"
if [ -z "$problems" ]; then
  ok every_dump_reaches_the_bucket
else
  bad every_dump_reaches_the_bucket "$problems"
fi

# --- a_successful_backup_writes_the_status --------------------------------------------------------

rfc3339='^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$'
problems=""
if ! jq -e . "$tmp/status.json" > /dev/null 2>&1; then
  problems="not JSON: $(cat "$tmp/status.json")"
else
  attempt="$(jq -r .last_attempt_at "$tmp/status.json")"
  [ "$(jq -r .last_attempt_ok "$tmp/status.json")" = true ] || problems="$problems last_attempt_ok is not true;"
  [ "$(jq -r .last_success_at "$tmp/status.json")" = "$attempt" ] || problems="$problems last_success_at differs from last_attempt_at;"
  [ "$(jq -r .last_error "$tmp/status.json")" = null ] || problems="$problems last_error is not null;"
  printf '%s' "$attempt" | grep -Eq "$rfc3339" || problems="$problems last_attempt_at '$attempt' is not RFC 3339 in UTC;"
  # The attempt's time is the run's, the stamp in every file name.
  [ "$(printf '%s' "$attempt" | tr -d ':-')" = "$stamps" ] || problems="$problems last_attempt_at $attempt is not the run's stamp $stamps;"
fi
# Replaced in one step: a rename gives the file a new inode, and leaves no temporary file behind.
case "$first_inode" in '' | *[!0-9]*) problems="$problems no status file after the first backup: $first_inode;" ;; esac
[ "$first_inode" != "$second_inode" ] || problems="$problems the status file kept its inode $first_inode, so it was written in place;"
[ -z "$status_leftovers" ] || problems="$problems /status also holds: $(echo $status_leftovers);"
if [ -z "$problems" ]; then
  ok a_successful_backup_writes_the_status
else
  bad a_successful_backup_writes_the_status "$problems"
fi

# --- no_secret_is_printed_or_passed_on_a_command_line ---------------------------------------------

problems=""
for secret_name in BACKUP_S3_KEY BACKUP_DB_PASSWORD; do
  secret="$s3_key"
  [ "$secret_name" = BACKUP_DB_PASSWORD ] && secret="$db_password"
  [ -n "$secret" ] || { problems="$problems $secret_name is empty in the env file;"; continue; }
  grep -qF -- "$secret" "$tmp/second.out" && problems="$problems the output holds $secret_name;"
  grep -qF -- "$secret" "$tmp/status.json" && problems="$problems the status file holds $secret_name;"
  grep -qF -- "$secret" "$tmp/argv.log" && problems="$problems an argument list holds $secret_name;"
done
curl_lines="$(grep -c '^curl ' "$tmp/argv.log")"
config_lines="$(grep '^curl ' "$tmp/argv.log" | grep -cE -- '--config /dev/fd/[0-9]+')"
[ "$curl_lines" -gt 0 ] && [ "$curl_lines" = "$config_lines" ] ||
  problems="$problems of $curl_lines curl runs, $config_lines read --config from a file descriptor;"
grep -q '^pg_dump ' "$tmp/argv.log" || problems="$problems no pg_dump run was recorded;"
grep -E '^curl ' "$tmp/argv.log" | grep -qE -- '(^| )(-u|--user)( |$)' && problems="$problems curl was given --user;"
if [ -z "$problems" ]; then
  ok no_secret_is_printed_or_passed_on_a_command_line
else
  bad no_secret_is_printed_or_passed_on_a_command_line "$problems"
fi

# --- a_failed_upload_is_a_failed_backup ---------------------------------------------------------

earlier_success="$(jq -r .last_success_at "$tmp/status.json" 2> /dev/null)"

compose exec -T s3stub touch /tmp/s3/refuse
before="$(files)"
out="$(backup_now 2>&1)"
failed_code=$?
after_failure="$(files)"
status > "$tmp/failed.json" 2>&1
compose exec -T s3stub rm -f /tmp/s3/refuse

problems=""
[ "$failed_code" -ne 0 ] || problems="$problems it exited 0;"
if jq -e . "$tmp/failed.json" > /dev/null 2>&1; then
  [ "$(jq -r .last_attempt_ok "$tmp/failed.json")" = false ] || problems="$problems last_attempt_ok is not false;"
  [ "$(jq -r .last_error "$tmp/failed.json")" = upload ] || problems="$problems last_error is $(jq -c .last_error "$tmp/failed.json"), not \"upload\";"
  [ "$(jq -r .last_success_at "$tmp/failed.json")" = "$earlier_success" ] ||
    problems="$problems last_success_at is $(jq -r .last_success_at "$tmp/failed.json"), not the earlier $earlier_success;"
  [ "$(jq -r .last_attempt_at "$tmp/failed.json")" != "$earlier_success" ] || problems="$problems last_attempt_at did not move;"
else
  problems="$problems the status is not JSON: $(cat "$tmp/failed.json");"
fi
# The failed run's own dumps are kept, one per database.
[ "$(new_files "$before" "$after_failure" | grep -c .)" = "$(databases | grep -c .)" ] ||
  problems="$problems the failed run kept $(new_files "$before" "$after_failure" | grep -c .) files;"
if [ -z "$problems" ]; then
  ok a_failed_upload_is_a_failed_backup
else
  bad a_failed_upload_is_a_failed_backup "$problems ($(printf '%s' "$out" | tail -n 2 | tr '\n' ' '))"
fi

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
