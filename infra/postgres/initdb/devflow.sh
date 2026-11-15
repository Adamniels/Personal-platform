#!/usr/bin/env bash
# Creates Dev flow's database and its two roles: devflow_owner, which owns the
# tables and runs migrations, and devflow_app, which Dev flow connects as while it
# serves. Tables, and grants on them, belong in Dev flow's migrations rather than
# here.
#
# Safe to run again. Postgres runs it once, when the volume is first
# initialised, and a volume that already exists is given Dev flow's database
# by running it by hand, from infra/:
#
#   docker compose exec -T postgres bash /docker-entrypoint-initdb.d/devflow.sh
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
  --set owner_password="$DEVFLOW_OWNER_PASSWORD" \
  --set app_password="$DEVFLOW_APP_PASSWORD" <<'SQL'
-- \gexec runs each row the query returns as a statement, so a statement runs
-- only when its WHERE finds the object missing. The password is quoted by
-- format's %L, and the statement is executed rather than printed.
SELECT format('CREATE ROLE devflow_owner LOGIN PASSWORD %L', :'owner_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'devflow_owner')
\gexec
SELECT format('CREATE ROLE devflow_app LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'devflow_app')
\gexec
SELECT 'CREATE DATABASE devflow OWNER devflow_owner'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'devflow')
\gexec

-- Every role is in PUBLIC, which by default may connect to any database and
-- create temporary tables in it. Only Dev flow's own roles belong here.
REVOKE ALL ON DATABASE devflow FROM PUBLIC;
GRANT CONNECT ON DATABASE devflow TO devflow_app;
SQL
