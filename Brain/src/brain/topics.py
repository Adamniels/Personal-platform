"""Topics, the vocabulary a user's memory is about. See "Topic vocabulary" in
docs/event-model.md, and the `topics` table in docs/data-model.md.

A topic is canonical or provisional, and may be an alias: marked the same as
another, with `alias_of` naming it. Nothing else is rewritten when that
happens. Whatever names the alias keeps naming it, and is read as the target,
through `resolve`, so separating the two again loses nothing.

Every function here takes a connection from `brain.db.transaction`, so it
acts for that transaction's user, and row level security hides every other
user's rows: a topic of theirs is not found, exactly like one that never
existed. Each function that changes the vocabulary first takes the user's
lock, `lock_vocabulary`, since its checks read rows another change could be
writing.
"""

import re
from dataclasses import dataclass
from datetime import datetime
from typing import Literal
from uuid import UUID, uuid7

from psycopg import AsyncConnection, errors
from psycopg.rows import TupleRow

from brain.db import CURRENT_USER_SQL, USER_SETTING
from brain.errors import Conflict, NotFound, Refused

type Connection = AsyncConnection[TupleRow]
type State = Literal["canonical", "provisional"]

# Lowercase letters, digits and hyphens, starting with a letter or digit, 1 to
# 80 characters. The migration checks the same rule.
SLUG_MAX = 80
SLUG_PATTERN = rf"^[a-z0-9][a-z0-9-]{{0,{SLUG_MAX - 1}}}$"


@dataclass(frozen=True)
class Topic:
    id: UUID
    slug: str
    name: str
    state: State
    alias_of: UUID | None
    created_at: datetime

    @property
    def resolves_to(self) -> UUID:
        """The topic this one is read as: its target if it is an alias, since
        an alias never points at another alias, and itself otherwise."""
        return self.alias_of or self.id


COLUMNS = "id, slug, name, state, alias_of, created_at"


def slug_from(name: str) -> str | None:
    """The slug a name gives when none is chosen: lowercased, every run of
    anything but ASCII letters and digits one hyphen, no hyphen at either end,
    and at most 80 characters. None when nothing is left."""
    slug = re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
    return slug[:SLUG_MAX].rstrip("-") or None


async def lock_vocabulary(conn: Connection) -> None:
    """Waits for, then holds until the transaction ends, the lock on the
    current user's vocabulary. Every change to it takes this first, so two
    changes cannot both pass their checks on rows the other is writing: an
    alias made the target of another, or two relations that close a loop.
    Other users' changes never wait for it."""
    await conn.execute(
        "SELECT pg_advisory_xact_lock(hashtextextended("
        f"'brain.topics:' || current_setting('{USER_SETTING}'), 0))"
    )


async def create(
    conn: Connection, name: str, slug: str, *, now: datetime, state: State = "canonical"
) -> Topic:
    """A new topic for the current user. Through the API it is always
    canonical; a provisional one is made by an event about a topic the user
    does not have yet. A slug the user already has is a `Conflict`."""
    try:
        cursor = await conn.execute(
            f"INSERT INTO topics ({COLUMNS}, user_id)"
            f" VALUES (%s, %s, %s, %s, NULL, %s, {CURRENT_USER_SQL})"
            f" RETURNING {COLUMNS}",
            (uuid7(), slug, name, state, now),
        )
    except errors.UniqueViolation as err:
        raise Conflict(f"there is already a topic {slug}") from err
    row = await cursor.fetchone()
    assert row is not None
    return Topic(*row)


async def get(conn: Connection, topic_id: UUID) -> Topic | None:
    cursor = await conn.execute(
        f"SELECT {COLUMNS} FROM topics WHERE id = %s", (topic_id,)
    )
    row = await cursor.fetchone()
    return Topic(*row) if row is not None else None


async def everything(conn: Connection) -> list[Topic]:
    """Every topic the current user has, aliases included, by slug."""
    cursor = await conn.execute(f"SELECT {COLUMNS} FROM topics ORDER BY slug")
    return [Topic(*row) for row in await cursor.fetchall()]


async def resolve(conn: Connection, topic_id: UUID) -> UUID | None:
    """The canonical topic `topic_id` is read as, or None if the user has no
    such topic."""
    topic = await get(conn, topic_id)
    return topic.resolves_to if topic is not None else None


async def _found(conn: Connection, topic_id: UUID) -> Topic:
    topic = await get(conn, topic_id)
    if topic is None:
        raise NotFound
    return topic


async def _set(conn: Connection, topic_id: UUID, column: str, value: object) -> Topic:
    # Only the two columns the app role may update, see the migration.
    assert column in ("state", "alias_of")
    cursor = await conn.execute(
        f"UPDATE topics SET {column} = %s WHERE id = %s RETURNING {COLUMNS}",
        (value, topic_id),
    )
    row = await cursor.fetchone()
    assert row is not None
    return Topic(*row)


async def promote(conn: Connection, topic_id: UUID) -> Topic:
    """Makes a provisional topic canonical. A canonical one is left as it is.
    An alias is refused: it is read as its target, so it is the target that
    is promoted."""
    await lock_vocabulary(conn)
    topic = await _found(conn, topic_id)
    if topic.alias_of is not None:
        raise Refused("an alias is not promoted, its target is")
    if topic.state == "canonical":
        return topic
    return await _set(conn, topic_id, "state", "canonical")


async def same_as(conn: Connection, topic_id: UUID, target_id: UUID) -> Topic:
    """Marks the topic the same as `target_id`, so it is read as the target
    from now on, and returns it. Only `alias_of` changes.

    Refused when the result would not be a vocabulary: the topic itself as
    its target, a target that is not there, is an alias or is not canonical,
    a topic that is already an alias, or one that others are aliases of,
    which would leave them pointing at an alias."""
    await lock_vocabulary(conn)
    topic = await _found(conn, topic_id)
    if target_id == topic_id:
        raise Refused("a topic is not the same as itself")
    target = await get(conn, target_id)
    if target is None:
        raise Refused("there is no such topic to be the same as")
    if topic.alias_of is not None:
        raise Refused("this topic is already an alias, separate it first")
    if target.alias_of is not None:
        raise Refused("the target is an alias, point at its target instead")
    if target.state != "canonical":
        raise Refused("the target is provisional, promote it first")
    cursor = await conn.execute(
        "SELECT EXISTS (SELECT FROM topics WHERE alias_of = %s)", (topic_id,)
    )
    row = await cursor.fetchone()
    if row is not None and row[0]:
        raise Refused("other topics are aliases of this one")
    return await _set(conn, topic_id, "alias_of", target_id)


async def separate(conn: Connection, topic_id: UUID) -> Topic:
    """Makes an alias its own topic again, and returns it. Everything that
    names it was never changed, so it is read as this topic again. A topic
    that is not an alias is left as it is."""
    await lock_vocabulary(conn)
    topic = await _found(conn, topic_id)
    if topic.alias_of is None:
        return topic
    return await _set(conn, topic_id, "alias_of", None)
