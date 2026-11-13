"""`brain migrate`: applies the plain SQL files in `Brain/migrations/` as the
owner role, forward only. See "Migrating" in Brain/README.md.

Each file is `NNNN_snake_description.sql`. A file not yet in the ledger,
`schema_migrations`, is applied in its own transaction together with its
ledger row, so a file either applies whole and is recorded or leaves nothing.
A failing file stops the run, and no later file runs. A session advisory lock
keeps two runs from applying the same file at once.

The ledger is checked against the files before anything is applied: an
applied file that was renamed or removed, or a new file numbered below one
already applied, is refused rather than guessed at, since forward only means
the files are the history.
"""

import os
import re
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

import psycopg

# The files the command applies: Brain/migrations/, beside src/.
MIGRATIONS = Path(__file__).resolve().parents[2] / "migrations"

# The session advisory lock every run takes, the same number everywhere.
# Arbitrary, but fixed: "brain" in ASCII.
LOCK_KEY = 0x627261696E

FILE_NAME = re.compile(r"^(\d{4})_([a-z0-9]+(?:_[a-z0-9]+)*)\.sql$")

LEDGER = """
CREATE TABLE IF NOT EXISTS schema_migrations (
    version bigint PRIMARY KEY,
    name text NOT NULL,
    applied_at timestamptz NOT NULL
)
"""


class MigrationError(Exception):
    """A run that cannot go on. Nothing after the failing step was applied."""


@dataclass(frozen=True)
class Migration:
    version: int
    name: str
    path: Path

    @property
    def file(self) -> str:
        return self.path.name


def files(directory: Path) -> list[Migration]:
    """Every migration in `directory`, in order. Any other `.sql` file, or two
    with one version, is an error, since the order would be a guess."""
    found: dict[int, Migration] = {}
    for path in sorted(directory.glob("*.sql")):
        match = FILE_NAME.match(path.name)
        if match is None:
            raise MigrationError(
                f"{path.name}: not a migration name, expected NNNN_snake_description.sql"
            )
        version = int(match.group(1))
        if version in found:
            raise MigrationError(
                f"{path.name}: version {version} is also {found[version].file}"
            )
        found[version] = Migration(version, match.group(2), path)
    return [found[version] for version in sorted(found)]


def run(
    conn: psycopg.Connection[tuple[object, ...]], directory: Path
) -> list[Migration]:
    """Applies what `directory` holds and the ledger lacks, in order, and
    returns what it applied. `conn` is the owner's, and is left open."""
    migrations = files(directory)
    conn.autocommit = True
    # Session scoped, so it is held across the transactions below, and freed
    # when the connection closes even if this process dies.
    conn.execute("SELECT pg_advisory_lock(%s)", (LOCK_KEY,))
    try:
        conn.execute(LEDGER)
        applied = {
            int(str(version)): str(name)
            for version, name in conn.execute(
                "SELECT version, name FROM schema_migrations"
            )
        }
        pending = check(migrations, applied)
        for migration in pending:
            apply(conn, migration)
        return pending
    finally:
        conn.execute("SELECT pg_advisory_unlock(%s)", (LOCK_KEY,))


def check(migrations: Sequence[Migration], applied: dict[int, str]) -> list[Migration]:
    """What is still to apply, once the files agree with the ledger."""
    by_version = {m.version: m for m in migrations}
    for version, name in sorted(applied.items()):
        migration = by_version.get(version)
        if migration is None:
            raise MigrationError(
                f"{version:04d}_{name}.sql was applied and is no longer here"
            )
        if migration.name != name:
            raise MigrationError(
                f"{migration.file} was applied as {version:04d}_{name}.sql, an applied file is never renamed"
            )
    newest = max(applied, default=-1)
    pending = [m for m in migrations if m.version not in applied]
    late = [m.file for m in pending if m.version < newest]
    if late:
        raise MigrationError(
            f"{', '.join(late)}: numbered below {newest:04d}, which is applied, so it would run out of order"
        )
    return pending


def apply(conn: psycopg.Connection[tuple[object, ...]], migration: Migration) -> None:
    sql = migration.path.read_text()
    try:
        with conn.transaction():
            # No parameters, so the file goes as one simple query and may hold
            # several statements.
            conn.execute(sql.encode())
            conn.execute(
                "INSERT INTO schema_migrations (version, name, applied_at) VALUES (%s, %s, now())",
                (migration.version, migration.name),
            )
    except psycopg.Error as err:
        raise MigrationError(
            f"{migration.file} failed and was rolled back: {err}"
        ) from err


def main() -> int:
    """`brain migrate`, as the owner, from BRAIN_OWNER_DATABASE_URL. Prints
    one line per file applied, or that there was nothing to do."""
    url = os.environ.get("BRAIN_OWNER_DATABASE_URL")
    if not url:
        print(
            "BRAIN_OWNER_DATABASE_URL is not set, see Brain/README.md", file=sys.stderr
        )
        return 1
    try:
        conn = psycopg.connect(url)
    except psycopg.Error as err:
        # psycopg's message names the host, the role and the reason, never the
        # password, and the connection string itself is never printed.
        print(f"could not connect to the database as the owner: {err}", file=sys.stderr)
        return 1
    try:
        with conn:
            applied = run(conn, MIGRATIONS)
    except MigrationError as err:
        print(f"migration failed: {err}", file=sys.stderr)
        return 1
    if not applied:
        print("nothing to do, every migration is applied")
    for migration in applied:
        print(f"applied {migration.version} {migration.name}")
    return 0
