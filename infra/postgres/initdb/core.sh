#!/usr/bin/env bash
# Creates the core's database and its two roles: core_owner, which owns the
# tables and runs migrations, and core_app, which the running core connects as.
# Tables, and grants on them, belong in the core's migrations rather than here.
#
# Safe to run again. Postgres runs it once, when the volume is first
# initialised, and it can be run again by hand, from infra/:
#
#   docker compose exec -T postgres bash /docker-entrypoint-initdb.d/core.sh
#
# Each role and the database are created only when missing, so a second run
# never changes a password; the database's privileges are applied every time,
# and are the same each time. See infra/README.md.
set -euo pipefail

# Set by the image's entrypoint during initialisation, and by CI's setup step;
# by hand inside the container neither is set, and these are the image's own
# defaults.
user="${POSTGRES_USER:-postgres}"
database="${POSTGRES_DB:-$user}"

psql --set ON_ERROR_STOP=1 \
  --username "$user" --dbname "$database" \
  --set owner_password="$CORE_OWNER_PASSWORD" \
  --set app_password="$CORE_APP_PASSWORD" <<'SQL'
-- \gexec runs each row the query returns as a statement, so a statement runs
-- only when its WHERE finds the object missing. The password is quoted by
-- format's %L, and the statement is executed rather than printed.
SELECT format('CREATE ROLE core_owner LOGIN PASSWORD %L', :'owner_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'core_owner')
\gexec
SELECT format('CREATE ROLE core_app LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'core_app')
\gexec
SELECT 'CREATE DATABASE core OWNER core_owner'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'core')
\gexec

-- Every role is in PUBLIC, which by default may connect to any database and
-- create temporary tables in it. Only the core's own roles belong here.
REVOKE ALL ON DATABASE core FROM PUBLIC;
GRANT CONNECT ON DATABASE core TO core_app;
SQL
