#!/usr/bin/env bash
# Creates review's database and its two roles: review_owner, which owns the
# tables and runs migrations, and review_app, which review connects as while it
# serves. Tables, and grants on them, belong in review's migrations rather than
# here.
#
# Safe to run again. Postgres runs it once, when the volume is first
# initialised, and a volume that already exists is given review's database
# by running it by hand, from infra/:
#
#   docker compose exec -T postgres bash /docker-entrypoint-initdb.d/review.sh
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
  --set owner_password="$REVIEW_OWNER_PASSWORD" \
  --set app_password="$REVIEW_APP_PASSWORD" <<'SQL'
-- \gexec runs each row the query returns as a statement, so a statement runs
-- only when its WHERE finds the object missing. The password is quoted by
-- format's %L, and the statement is executed rather than printed.
SELECT format('CREATE ROLE review_owner LOGIN PASSWORD %L', :'owner_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'review_owner')
\gexec
SELECT format('CREATE ROLE review_app LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'review_app')
\gexec
SELECT 'CREATE DATABASE review OWNER review_owner'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'review')
\gexec

-- Every role is in PUBLIC, which by default may connect to any database and
-- create temporary tables in it. Only review's own roles belong here.
REVOKE ALL ON DATABASE review FROM PUBLIC;
GRANT CONNECT ON DATABASE review TO review_app;
SQL
