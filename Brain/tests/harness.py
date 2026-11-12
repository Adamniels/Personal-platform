"""A database of its own for every test that needs one, on the real Postgres:
compose locally, the service container in CI. The brain's counterpart of
`Core/tests/common/mod.rs`.

Once per test session, a template is created, given the grants
`infra/postgres/initdb/brain.sh` gives the brain's database, and migrated as
brain_owner from `Brain/migrations/`. Each test then gets a copy of it and
connects as brain_app, so it tests what the running brain is. A test that
passes drops its copy; one that fails leaves it to look at, and a later run
sweeps it away.

Two connection strings, both from the environment, both required: unset is a
failure, never a skip, so a run that lost one cannot pass by testing nothing.

- `BRAIN_TEST_ADMIN_DATABASE_URL`, the Postgres superuser, for creating
  databases and arranging what the app role cannot. Only tests hold it.
- `BRAIN_APP_DATABASE_URL`, the app role's own, the one the running brain
  uses, pointed at each test's database.

Locally, from Brain/:
  BRAIN_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" \\
  BRAIN_APP_DATABASE_URL="postgres://brain_app:$(grep '^BRAIN_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/brain" \\
  uv run pytest
"""

import itertools
import os
import re
import time
from pathlib import Path

import psycopg
from psycopg import sql
from psycopg.conninfo import make_conninfo

MIGRATIONS = Path(__file__).resolve().parents[1] / "migrations"

# Older than this, a leftover test database or template belongs to a run that
# has finished, here or on another machine, and is swept.
SWEEP_AFTER_SECONDS = 10 * 60

STARTED_MS = int(time.time() * 1000)
_counter = itertools.count()


def admin_url() -> str:
    url = os.environ.get("BRAIN_TEST_ADMIN_DATABASE_URL")
    assert url, "BRAIN_TEST_ADMIN_DATABASE_URL is not set, see Brain/tests/conftest.py"
    return url


def app_url(database: str) -> str:
    url = os.environ.get("BRAIN_APP_DATABASE_URL")
    assert url, "BRAIN_APP_DATABASE_URL is not set, see Brain/tests/conftest.py"
    return make_conninfo(url, dbname=database)


def admin_connection(
    database: str = "postgres",
) -> psycopg.Connection[tuple[object, ...]]:
    """A superuser connection, in autocommit, since CREATE and DROP DATABASE
    cannot run inside a transaction."""
    return psycopg.connect(make_conninfo(admin_url(), dbname=database), autocommit=True)


def owner_connection(database: str) -> psycopg.Connection[tuple[object, ...]]:
    """A superuser connection acting as brain_owner, as `brain migrate` runs,
    so what it creates is the owner's and the owner's default privileges
    apply. The owner's password is not something tests hold."""
    conn = admin_connection(database)
    conn.execute("SET ROLE brain_owner")
    return conn


def unique_name(kind: str) -> str:
    """brain_<kind>_<ms at session start>_<pid>_<counter>: unique within and
    across runs, and safe as an identifier."""
    return f"brain_{kind}_{STARTED_MS}_{os.getpid()}_{next(_counter)}"


def create_database(name: str, template: str | None = None) -> None:
    with admin_connection() as admin:
        if template is None:
            admin.execute(
                sql.SQL("CREATE DATABASE {} OWNER brain_owner").format(
                    sql.Identifier(name)
                )
            )
        else:
            admin.execute(
                sql.SQL("CREATE DATABASE {} TEMPLATE {} OWNER brain_owner").format(
                    sql.Identifier(name), sql.Identifier(template)
                )
            )
        # Database privileges belong to the database, not its contents, so a
        # copy does not inherit the template's. The same two lines as brain.sh.
        admin.execute(
            sql.SQL("REVOKE ALL ON DATABASE {} FROM PUBLIC").format(
                sql.Identifier(name)
            )
        )
        admin.execute(
            sql.SQL("GRANT CONNECT ON DATABASE {} TO brain_app").format(
                sql.Identifier(name)
            )
        )


def drop_database(name: str) -> None:
    with admin_connection() as admin:
        admin.execute(
            sql.SQL("DROP DATABASE IF EXISTS {} WITH (FORCE)").format(
                sql.Identifier(name)
            )
        )


def sweep() -> None:
    """Drops what earlier runs left: databases of failed tests, and templates,
    but only those stamped long enough before this session started, so a run
    going at the same time keeps its own."""
    with admin_connection() as admin:
        names = [
            row[0]
            for row in admin.execute(
                r"SELECT datname::text FROM pg_database WHERE datname ~ '^brain_(test|tpl|empty)_\d+_'"
            )
        ]
    for name in names:
        match = re.match(r"^brain_[a-z]+_(\d+)_", str(name))
        if match and int(match.group(1)) + SWEEP_AFTER_SECONDS * 1000 < STARTED_MS:
            drop_database(str(name))
