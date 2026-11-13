"""Topics, the vocabulary a user's memory is about. See "Topic vocabulary" in
docs/event-model.md, and the `topics` table in docs/data-model.md.

A topic is canonical or provisional, and may be an alias: marked the same as
another, with `alias_of` naming it. Topics are related to each other, one
broader than another, in `topic_relations`, which are ended rather than
deleted. See "Relations" in docs/event-model.md. Nothing else is rewritten when that
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

# The kinds of relation between topics. A text column, checked here rather
# than in the database, so a new kind is a change to this list and not a
# migration. `from_topic` is broader than `to_topic`.
RELATION_KINDS = ("broader_than", "related_to")


@dataclass(frozen=True)
class TopicRelation:
    id: UUID
    from_topic: UUID
    to_topic: UUID
    kind: str
    created_at: datetime
    # None while it holds.
    ended_at: datetime | None


RELATION_COLUMNS = "id, from_topic, to_topic, kind, created_at, ended_at"


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


async def by_slug(conn: Connection, slug: str) -> Topic | None:
    cursor = await conn.execute(
        f"SELECT {COLUMNS} FROM topics WHERE slug = %s", (slug,)
    )
    row = await cursor.fetchone()
    return Topic(*row) if row is not None else None


async def named(conn: Connection, slug: str, *, now: datetime) -> Topic:
    """The current user's topic with `slug`, as it is, an alias included, or
    a new provisional one named by its slug when there is none. What an event
    about a topic is recorded on: created silently, see "Topic vocabulary" in
    docs/event-model.md. Created under the user's lock, so two events naming
    the same new slug make one topic."""
    found = await by_slug(conn, slug)
    if found is not None:
        return found
    await lock_vocabulary(conn)
    found = await by_slug(conn, slug)
    if found is not None:
        return found
    return await create(conn, slug, slug, now=now, state="provisional")


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
    which would leave them pointing at an alias. And when one of the two is
    under the other, since read as one topic it would be under itself."""
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
    # One under the other would, once they are read as one, be under itself.
    if target_id in await narrower(conn, topic_id) or topic_id in await narrower(
        conn, target_id
    ):
        raise Refused("one of the two is under the other")
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


async def narrower(conn: Connection, topic_id: UUID) -> set[UUID]:
    """Every topic under `topic_id`, directly or through others, by the
    broader_than relations that hold, with every topic on the way read
    through its alias. Asked about an alias, it answers about the target.
    The topic itself is never in it, since no relation closes a loop."""
    root = await resolve(conn, topic_id)
    if root is None:
        return set()
    cursor = await conn.execute(
        """
        WITH RECURSIVE edges AS (
            SELECT coalesce(broader.alias_of, broader.id) AS broader,
                   coalesce(under.alias_of, under.id) AS narrower
            FROM topic_relations r
            JOIN topics broader ON broader.id = r.from_topic
            JOIN topics under ON under.id = r.to_topic
            WHERE r.kind = 'broader_than' AND r.ended_at IS NULL
        ), below (id) AS (
            SELECT narrower FROM edges WHERE broader = %(root)s
            UNION
            SELECT edges.narrower FROM edges JOIN below ON edges.broader = below.id
        )
        SELECT id FROM below WHERE id <> %(root)s
        """,
        {"root": root},
    )
    return {row[0] for row in await cursor.fetchall()}


async def add_broader(
    conn: Connection, topic_id: UUID, broader_id: UUID, *, now: datetime
) -> TopicRelation:
    """Places the topic under `broader_id`, and returns the relation. Both are
    kept as given, and read through their aliases.

    Refused when the broader topic is not there, or when it would close a
    loop: the two read as one topic, or the broader one already under this
    one, directly or through others. One that already holds is a
    `Conflict`."""
    await lock_vocabulary(conn)
    under = await _found(conn, topic_id)
    over = await get(conn, broader_id)
    if over is None:
        raise Refused("there is no such topic to be under")
    if over.resolves_to == under.resolves_to:
        raise Refused("a topic is not under itself")
    if over.resolves_to in await narrower(conn, under.resolves_to):
        raise Refused("the broader topic is already under this one")
    return await _relate(conn, broader_id, topic_id, "broader_than", now=now)


async def add_related(
    conn: Connection, topic_id: UUID, related_id: UUID, *, now: datetime
) -> TopicRelation:
    """Relates the topic to `related_id`, and returns the relation, from this
    topic to the other. Related has no direction: it is read either way, and
    through aliases on both ends. Neither is under the other, so it never
    closes a loop, and asking about one lists the other without bringing
    what is known about it.

    Refused when the other topic is not there, or the two are read as one
    topic. One that already holds, either way round, read through aliases,
    is a `Conflict`."""
    await lock_vocabulary(conn)
    one = await _found(conn, topic_id)
    other = await get(conn, related_id)
    if other is None:
        raise Refused("there is no such topic to be related to")
    if other.resolves_to == one.resolves_to:
        raise Refused("a topic is not related to itself")
    cursor = await conn.execute(
        "SELECT EXISTS (SELECT FROM topic_relations r"
        " JOIN topics a ON a.id = r.from_topic JOIN topics b ON b.id = r.to_topic"
        " WHERE r.kind = 'related_to' AND r.ended_at IS NULL"
        " AND ARRAY[coalesce(a.alias_of, a.id), coalesce(b.alias_of, b.id)]"
        "  @> ARRAY[%(one)s::uuid, %(other)s::uuid])",
        {"one": one.resolves_to, "other": other.resolves_to},
    )
    row = await cursor.fetchone()
    if row is not None and row[0]:
        raise Conflict("that relation already holds")
    return await _relate(conn, topic_id, related_id, "related_to", now=now)


async def _relate(
    conn: Connection, from_topic: UUID, to_topic: UUID, kind: str, *, now: datetime
) -> TopicRelation:
    if kind not in RELATION_KINDS:
        raise Refused(f"{kind} is not a kind of relation between topics")
    try:
        cursor = await conn.execute(
            f"INSERT INTO topic_relations ({RELATION_COLUMNS}, user_id)"
            f" VALUES (%s, %s, %s, %s, %s, NULL, {CURRENT_USER_SQL})"
            f" RETURNING {RELATION_COLUMNS}",
            (uuid7(), from_topic, to_topic, kind, now),
        )
    except errors.UniqueViolation as err:
        raise Conflict("that relation already holds") from err
    row = await cursor.fetchone()
    assert row is not None
    return TopicRelation(*row)


async def end_relation(
    conn: Connection, relation_id: UUID, *, now: datetime
) -> TopicRelation:
    """Ends a relation at `now`, and returns it. The row stays, so what held
    and when stays readable. One that has already ended keeps the time it
    ended."""
    cursor = await conn.execute(
        f"UPDATE topic_relations SET ended_at = %s"
        f" WHERE id = %s AND ended_at IS NULL RETURNING {RELATION_COLUMNS}",
        (now, relation_id),
    )
    row = await cursor.fetchone()
    if row is None:
        cursor = await conn.execute(
            f"SELECT {RELATION_COLUMNS} FROM topic_relations WHERE id = %s",
            (relation_id,),
        )
        row = await cursor.fetchone()
    if row is None:
        raise NotFound
    return TopicRelation(*row)
