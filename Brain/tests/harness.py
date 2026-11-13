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
from urllib.parse import urlsplit, urlunsplit

import psycopg

from brain import scratch

MIGRATIONS = Path(__file__).resolve().parents[1] / "migrations"

# The smallest environment the settings load from, for a test that builds the
# app without a database: the one required string, which nothing connects to.
MINIMAL_ENV = {
    "BRAIN_APP_DATABASE_URL": "postgres://brain_app:secret-a1b2@127.0.0.1:5432/brain"
}

# Older than this, a leftover test database or template belongs to a run that
# has finished, here or on another machine, and is swept.
SWEEP_AFTER_SECONDS = 10 * 60

STARTED_MS = int(time.time() * 1000)
_counter = itertools.count()


def admin_url() -> str:
    url = os.environ.get("BRAIN_TEST_ADMIN_DATABASE_URL")
    assert url, "BRAIN_TEST_ADMIN_DATABASE_URL is not set, see Brain/tests/harness.py"
    return url


def app_url(database: str) -> str:
    """The app role's URL, pointed at `database`: a URL like the setting
    requires, not psycopg's key=value form."""
    url = os.environ.get("BRAIN_APP_DATABASE_URL")
    assert url, "BRAIN_APP_DATABASE_URL is not set, see Brain/tests/harness.py"
    return urlunsplit(urlsplit(url)._replace(path=f"/{database}"))


def admin_connection(
    database: str = "postgres",
) -> psycopg.Connection[tuple[object, ...]]:
    """A superuser connection, in autocommit, see `brain.scratch`."""
    return scratch.admin_connection(admin_url(), database)


def owner_connection(database: str) -> psycopg.Connection[tuple[object, ...]]:
    """A superuser connection acting as brain_owner, as `brain migrate` runs.
    The owner's password is not something tests hold."""
    return scratch.owner_connection(admin_url(), database)


def unique_name(kind: str) -> str:
    """brain_<kind>_<ms at session start>_<pid>_<counter>: unique within and
    across runs, and safe as an identifier."""
    return f"brain_{kind}_{STARTED_MS}_{os.getpid()}_{next(_counter)}"


def create_database(name: str, template: str | None = None) -> None:
    scratch.create(admin_url(), name, template)


def drop_database(name: str) -> None:
    scratch.drop(admin_url(), name)


def sweep() -> None:
    """Drops what earlier runs left: databases of failed tests, templates,
    and an evaluation's stopped before it dropped its own, but only those
    stamped long enough before this session started, so a run going at
    the same time keeps its own."""
    with admin_connection() as admin:
        names = [
            row[0]
            for row in admin.execute(
                r"SELECT datname::text FROM pg_database WHERE datname ~ '^brain_(test|tpl|empty|eval)_\d+_'"
            )
        ]
    for name in names:
        match = re.match(r"^brain_[a-z]+_(\d+)_", str(name))
        if match and int(match.group(1)) + SWEEP_AFTER_SECONDS * 1000 < STARTED_MS:
            drop_database(str(name))
