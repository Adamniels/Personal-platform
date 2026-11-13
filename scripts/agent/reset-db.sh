#!/usr/bin/env bash
# Recreates the core's and the brain's databases empty, so every agent run starts from no data and
# the slice's migrations apply from scratch. The Mac mini's databases are disposable test data:
# nothing in them is kept, and no branch's migrations survive into the next run.
#
#   bash scripts/agent/reset-db.sh
#
# Runs psql inside the compose `postgres` service as the `postgres` superuser over the local socket,
# so no password is read. It mirrors infra/postgres/initdb/core.sh and brain.sh for the databases
# alone: drops `core` and `brain` (closing their connections), creates each owned by its owner role,
# revokes PUBLIC and grants CONNECT to its app role. The roles are kept, so brain.sh must have run
# once on the volume, see "The databases" in infra/README.md. Migrations are then the core's
# `migrate` command and the brain's `brain migrate`.
# Works from any directory: it runs from the repository root, where `--project-directory infra`
# finds the compose file.
# Exit codes: 0 ok, 1 when docker or psql fails (the stack is down, or a statement failed).
set -uo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd -P)"
cd "$root" || { echo "database reset failed: cannot enter $root" >&2; exit 1; }

# One statement per line on stdin, not `-c`, since DROP DATABASE cannot run inside the single
# transaction psql wraps a multi-statement -c in.
if ! docker compose --project-directory infra exec -T postgres \
  psql -U postgres -d postgres -v ON_ERROR_STOP=1 <<'SQL'
DROP DATABASE IF EXISTS core WITH (FORCE);
CREATE DATABASE core OWNER core_owner;
REVOKE ALL ON DATABASE core FROM PUBLIC;
GRANT CONNECT ON DATABASE core TO core_app;
DROP DATABASE IF EXISTS brain WITH (FORCE);
CREATE DATABASE brain OWNER brain_owner;
REVOKE ALL ON DATABASE brain FROM PUBLIC;
GRANT CONNECT ON DATABASE brain TO brain_app;
SQL
then
  echo "database reset failed: docker compose exec psql did not succeed" >&2
  exit 1
fi
