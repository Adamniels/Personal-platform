"""Isolation between users, enforced by Postgres and reached only through the
door, `brain.db.transaction`. Each test runs on its own copy of the migrated
database, as brain_app, the role the running brain connects as.

No real table exists yet, so the tests create a probe table, as brain_owner,
the way every scoped table is created: `user_id`, row level security enabled
and forced, and the one policy from "What holds for every table" in
`Brain/docs/data-model.md`."""

import re
import uuid
from pathlib import Path
from typing import cast

import psycopg
import pytest
from psycopg_pool import AsyncConnectionPool

from brain import db
from tests.harness import admin_connection, app_url, owner_connection

ALICE = uuid.UUID("0190a8f0-0000-7000-8000-00000000a11c")
BOB = uuid.UUID("0190a8f0-0000-7000-8000-000000000b0b")

PROBE = f"""
CREATE TABLE probe (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    note text NOT NULL
);
ALTER TABLE probe ENABLE ROW LEVEL SECURITY;
ALTER TABLE probe FORCE ROW LEVEL SECURITY;
CREATE POLICY probe_user ON probe USING (user_id = {db.CURRENT_USER_SQL});
"""

# The tables "What holds for every table" in Brain/docs/data-model.md names
# global: they hold nothing about a person, so they have no user_id and no
# policy. Every other table in public is scoped.
GLOBAL_TABLES = {"schema_migrations", "verbs"}


def create_probe(database: str) -> None:
    with owner_connection(database) as conn:
        conn.execute(PROBE)


async def insert(pool: AsyncConnectionPool, user: uuid.UUID, note: str) -> uuid.UUID:
    row_id = uuid.uuid4()
    async with db.transaction(pool, user) as conn:
        await conn.execute(
            "INSERT INTO probe (id, user_id, note) VALUES (%s, %s, %s)",
            (row_id, user, note),
        )
    return row_id


async def notes(pool: AsyncConnectionPool, user: uuid.UUID) -> list[str]:
    async with db.transaction(pool, user) as conn:
        cursor = await conn.execute("SELECT note FROM probe ORDER BY note")
        return [str(row[0]) for row in await cursor.fetchall()]


async def test_one_user_never_sees_another_users_rows(
    database: str, pool: AsyncConnectionPool
) -> None:
    create_probe(database)
    await insert(pool, ALICE, "alice's")
    bobs = await insert(pool, BOB, "bob's")

    assert await notes(pool, ALICE) == ["alice's"]
    assert await notes(pool, BOB) == ["bob's"]

    # Aimed at Bob's row by its id, as Alice: the row is not there for her.
    async with db.transaction(pool, ALICE) as conn:
        cursor = await conn.execute(
            "UPDATE probe SET note = 'changed' WHERE id = %s", (bobs,)
        )
        assert cursor.rowcount == 0
    assert await notes(pool, BOB) == ["bob's"]


async def test_an_insert_naming_another_user_is_refused(
    database: str, pool: AsyncConnectionPool
) -> None:
    create_probe(database)

    with pytest.raises(
        psycopg.errors.InsufficientPrivilege, match="row-level security"
    ):
        async with db.transaction(pool, ALICE) as conn:
            await conn.execute(
                "INSERT INTO probe (id, user_id, note) VALUES (%s, %s, 'planted')",
                (uuid.uuid4(), BOB),
            )

    assert await notes(pool, BOB) == []


async def test_a_query_with_no_user_set_returns_nothing(
    database: str, pool: AsyncConnectionPool
) -> None:
    create_probe(database)
    await insert(pool, ALICE, "alice's")

    # A connection that has never had the setting, outside the door.
    async with await psycopg.AsyncConnection.connect(app_url(database)) as fresh:
        cursor = await fresh.execute("SELECT count(*) FROM probe")
        assert await cursor.fetchone() == (0,)


async def test_the_user_does_not_leak_to_the_next_transaction_on_the_connection(
    database: str, pool: AsyncConnectionPool
) -> None:
    create_probe(database)
    await insert(pool, ALICE, "alice's")
    async with db.transaction(pool, ALICE) as conn:
        cursor = await conn.execute("SELECT pg_backend_pid()")
        alices_backend = await cursor.fetchone()

    # The pool holds one connection, so this is the one Alice's transaction
    # just returned. Taken outside the door, deliberately, as only a test may.
    async with pool.connection() as conn:
        cursor = await conn.execute("SELECT pg_backend_pid()")
        assert await cursor.fetchone() == alices_backend
        cursor = await conn.execute("SELECT count(*) FROM probe")
        assert await cursor.fetchone() == (0,)


async def test_the_app_role_cannot_bypass_or_disable_row_level_security(
    database: str, pool: AsyncConnectionPool
) -> None:
    create_probe(database)
    await insert(pool, ALICE, "alice's")

    refused = [
        # Allowed to set, but then any query a policy would filter is an error
        # rather than an unfiltered read.
        ("SET row_security = off; SELECT * FROM probe", "row-level security"),
        ("ALTER TABLE probe DISABLE ROW LEVEL SECURITY", "must be owner"),
        ("ALTER TABLE probe NO FORCE ROW LEVEL SECURITY", "must be owner"),
        ("DROP POLICY probe_user ON probe", "must be owner"),
        ("SET ROLE brain_owner", "permission denied"),
        (f"DELETE FROM probe WHERE user_id = '{ALICE}'", "permission denied"),
        ("TRUNCATE probe", "permission denied"),
    ]
    for statement, reason in refused:
        with pytest.raises(psycopg.errors.InsufficientPrivilege, match=reason):
            async with db.transaction(pool, ALICE) as conn:
                await conn.execute(statement)

    assert await notes(pool, ALICE) == ["alice's"]


async def test_the_door_takes_only_a_user_id(pool: AsyncConnectionPool) -> None:
    # A string would reach set_config unchecked, and '' or a typo would be a
    # transaction for nobody, so only a UUID gets in.
    with pytest.raises(TypeError):
        async with db.transaction(pool, str(ALICE)):  # type: ignore[arg-type]
            pass


def test_only_the_door_takes_a_connection_from_the_pool() -> None:
    package = Path(db.__file__).parent
    takers = re.compile(r"\.(connection|getconn)\(")
    found = [
        f"{path.name}:{number}"
        for path in sorted(package.rglob("*.py"))
        if path.name != "db.py"
        for number, line in enumerate(path.read_text().splitlines(), start=1)
        if takers.search(line)
    ]
    assert found == [], "only brain.db may take a connection from the pool"


def unguarded_tables(database: str) -> list[str]:
    """Each scoped table in public, and why it is not guarded: row level
    security not enabled, not forced, or no policy reading the user's id."""
    with admin_connection(database) as conn:
        rows = conn.execute(
            """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   EXISTS (
                       SELECT FROM pg_attribute a
                       WHERE a.attrelid = c.oid AND a.attname = 'user_id'
                         AND a.atttypid = 'uuid'::regtype AND a.attnotnull
                   ),
                   coalesce(array_agg(p.qual) FILTER (WHERE p.cmd = 'ALL'), '{}')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_policies p ON p.schemaname = n.nspname AND p.tablename = c.relname
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
            GROUP BY c.oid, c.relname, c.relrowsecurity, c.relforcerowsecurity
            ORDER BY c.relname
            """
        ).fetchall()
    found = []
    for name, enabled, forced, has_user_id, policies in rows:
        if name in GLOBAL_TABLES:
            continue
        if not enabled:
            found.append(f"{name}: row level security is not enabled")
        if not forced:
            found.append(f"{name}: row level security is not forced")
        if not has_user_id:
            found.append(f"{name}: no user_id uuid NOT NULL")
        quals = cast(list[str], policies)
        if not any("user_id" in q and db.USER_SETTING in q for q in quals):
            found.append(f"{name}: no policy for all commands on user_id")
    return found


def test_every_scoped_table_forces_row_level_security(database: str) -> None:
    # Every migration in Brain/migrations/ has run on this database, so a
    # table added later without its guard fails here.
    assert unguarded_tables(database) == []


def test_the_guard_finds_a_table_left_unguarded(database: str) -> None:
    # So the test above cannot pass by checking nothing.
    create_probe(database)
    with owner_connection(database) as conn:
        conn.execute("CREATE TABLE open_table (id uuid PRIMARY KEY, note text)")
        conn.execute(
            "CREATE TABLE half_guarded (id uuid PRIMARY KEY, user_id uuid NOT NULL)"
        )
        conn.execute("ALTER TABLE half_guarded ENABLE ROW LEVEL SECURITY")

    assert unguarded_tables(database) == [
        "half_guarded: row level security is not forced",
        "half_guarded: no policy for all commands on user_id",
        "open_table: row level security is not enabled",
        "open_table: row level security is not forced",
        "open_table: no user_id uuid NOT NULL",
        "open_table: no policy for all commands on user_id",
    ]
