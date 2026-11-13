"""Named things, the other kind of subject beside topics: a specific thing
such as a project, by its name, with an optional free text label. See "Scope
is topic and entity, never feature" in docs/trust-model.md, and the
`entities` table in docs/data-model.md.

A named thing is only a name the brain holds, never a link into a feature's
data. Its name is compared without letter case, so a user has one Personal
Platform however it is typed. It is never changed once made.

Every function takes a connection from `brain.db.transaction`, and acts for
that transaction's user.
"""

from dataclasses import dataclass
from datetime import datetime
from uuid import UUID, uuid7

from psycopg import AsyncConnection, errors
from psycopg.rows import TupleRow

from brain.db import CURRENT_USER_SQL
from brain.errors import Conflict

type Connection = AsyncConnection[TupleRow]


@dataclass(frozen=True)
class Entity:
    id: UUID
    name: str
    label: str | None
    created_at: datetime


COLUMNS = "id, name, label, created_at"


async def create(
    conn: Connection, name: str, label: str | None, *, now: datetime
) -> Entity:
    """A new named thing for the current user. A name the user already has,
    in any letter case, is a `Conflict`."""
    try:
        cursor = await conn.execute(
            f"INSERT INTO entities ({COLUMNS}, user_id)"
            f" VALUES (%s, %s, %s, %s, {CURRENT_USER_SQL}) RETURNING {COLUMNS}",
            (uuid7(), name, label, now),
        )
    except errors.UniqueViolation as err:
        raise Conflict(f"there is already a named thing {name}") from err
    row = await cursor.fetchone()
    assert row is not None
    return Entity(*row)


async def everything(conn: Connection) -> list[Entity]:
    """Every named thing the user has, by name, without letter case."""
    cursor = await conn.execute(
        f"SELECT {COLUMNS} FROM entities ORDER BY lower(name), id"
    )
    return [Entity(*row) for row in await cursor.fetchall()]
