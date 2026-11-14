#!/usr/bin/env bash
# Creates platform_backup, the role the backup job dumps every database as, and grants it CONNECT
# on every database that exists. It is the named bypass "Multi user, and how isolation is enforced"
# in High-level/docs/platform-architecture.md asks for: BYPASSRLS, since pg_dump turns row level
# security off and fails on any table whose policy would hide a row, and every scoped table here
# forces its policy even on its owner; and a member of pg_read_all_data, which reads every table
# and writes none. See "Backups" in infra/hosted/README.md.
#
# Safe to run again. The hosted Postgres runs it last when its volume is first created, mounted
# into the init directory as zz-backup-role.sh so it sorts after the scripts that create the
# databases; check.sh runs it after the migrations, and a release runs it, so a database added
# later is covered. By hand, inside the postgres container over its socket, from infra/hosted/:
#
#   docker compose -p <project> exec -T postgres bash /docker-entrypoint-initdb.d/zz-backup-role.sh
#
# The role is created only when missing, so a second run never changes its password; its
# attributes, its membership and the grants are applied every time, and are the same each time.
set -euo pipefail

# Set by the image's entrypoint during initialisation; by hand inside the container neither is
# set, and these are the image's own defaults.
user="${POSTGRES_USER:-postgres}"
database="${POSTGRES_DB:-$user}"

psql --set ON_ERROR_STOP=1 --quiet \
  --username "$user" --dbname "$database" \
  --set password="$BACKUP_DB_PASSWORD" <<'SQL'
-- A second run's grants of what it already has would each print a notice.
SET client_min_messages TO warning;

-- \gexec runs each row the query returns as a statement, so a statement runs only when its WHERE
-- finds the object missing. The password is quoted by format's %L.
SELECT format('CREATE ROLE platform_backup LOGIN BYPASSRLS PASSWORD %L', :'password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'platform_backup')
\gexec

-- Every time, so a role changed by hand is brought back: it logs in and bypasses row level
-- security, and can do nothing else of its own.
ALTER ROLE platform_backup LOGIN BYPASSRLS INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE
  NOREPLICATION;
GRANT pg_read_all_data TO platform_backup;

-- Each database revokes CONNECT from PUBLIC, so the role is granted it on each by name.
SELECT format('GRANT CONNECT ON DATABASE %I TO platform_backup', datname)
FROM pg_database
WHERE datallowconn AND NOT datistemplate
ORDER BY datname
\gexec
SQL
