#!/usr/bin/env bash
# Creates the core's database and its two roles. Runs once, when the Postgres
# volume is first initialised. Tables, and grants on them, belong in the core's
# migrations rather than here.
set -euo pipefail

psql --set ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set owner_password="$CORE_OWNER_PASSWORD" \
  --set app_password="$CORE_APP_PASSWORD" <<'SQL'
CREATE ROLE core_owner LOGIN PASSWORD :'owner_password';
CREATE ROLE core_app LOGIN PASSWORD :'app_password';

CREATE DATABASE core OWNER core_owner;

-- Every role is in PUBLIC, which by default may connect to any database and
-- create temporary tables in it. Only the core's own roles belong here.
REVOKE ALL ON DATABASE core FROM PUBLIC;
GRANT CONNECT ON DATABASE core TO core_app;
SQL
