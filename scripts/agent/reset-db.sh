#!/usr/bin/env bash
# Recreates every service's database empty, so every agent run starts from no data and the slice's
# migrations apply from scratch. The Mac mini's databases are disposable test data: nothing in
# them is kept, and no branch's migrations survive into the next run.
#
#   bash scripts/agent/reset-db.sh
#
# The databases are the manifest's, from `services.sh databases`, in manifest order. Runs psql
# inside the compose `postgres` service as the `postgres` superuser over the local socket, so no
# password is read. It mirrors infra/postgres/initdb/ for the databases alone: drops each (closing
# its connections), creates it owned by its owner role, revokes PUBLIC and grants CONNECT to its
# app role. The roles are kept, so each service's init script must have run once on the volume,
# see "The databases" in infra/README.md. Migrations are then `services.sh run <name> migrate`.
# Works from any directory: it runs from the repository root, where `--project-directory infra`
# finds the compose file.
# Exit codes: 0 ok, 1 when the manifest cannot be read or names something that is not an
# identifier, or when docker or psql fails (the stack is down, or a statement failed).
set -uo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd -P)"
cd "$root" || { echo "database reset failed: cannot enter $root" >&2; exit 1; }

if ! databases="$(bash "$root/scripts/agent/services.sh" databases)"; then
  echo "database reset failed: cannot read the databases from infra/services.json" >&2
  exit 1
fi

# The names go into SQL unquoted, so each must be a plain lowercase identifier.
sql=""
while read -r database owner app; do
  [ -z "$database" ] && continue
  for name in "$database" "$owner" "$app"; do
    if ! [[ "$name" =~ ^[a-z_][a-z0-9_]*$ ]]; then
      echo "database reset failed: '$name' in infra/services.json is not a lowercase identifier" >&2
      exit 1
    fi
  done
  sql+="DROP DATABASE IF EXISTS $database WITH (FORCE);
CREATE DATABASE $database OWNER $owner;
REVOKE ALL ON DATABASE $database FROM PUBLIC;
GRANT CONNECT ON DATABASE $database TO $app;
"
done <<< "$databases"

# One statement per line on stdin, not `-c`, since DROP DATABASE cannot run inside the single
# transaction psql wraps a multi-statement -c in.
if ! printf '%s' "$sql" | docker compose --project-directory infra exec -T postgres \
  psql -U postgres -d postgres -v ON_ERROR_STOP=1
then
  echo "database reset failed: docker compose exec psql did not succeed" >&2
  exit 1
fi
