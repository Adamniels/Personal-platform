"""Events, what happened: one table for every kind, see "The shape" and "One
table for all events" in docs/event-model.md, and the `events` table in
docs/data-model.md.

An event is recorded once and never changed: the service may insert and
read, and the migration revokes the rest. Sent again with the same origin and
idempotency key, it is the event already recorded, so a feature may retry
freely. An event about a topic is recorded on the topic it names, an alias
included, and read on whatever that topic resolves to now, so merging or
separating topics never rewrites one.

Every function takes a connection from `brain.db.transaction`, and acts for
that transaction's user.
"""

from dataclasses import dataclass
from datetime import datetime
from typing import Any, Literal
from uuid import UUID, uuid7

from psycopg import AsyncConnection
from psycopg.rows import TupleRow
from psycopg.types.json import Jsonb

from brain import entities, topics
from brain.db import CURRENT_USER_SQL
from brain.errors import Refused

type Connection = AsyncConnection[TupleRow]
type Precision = Literal["instant", "day", "month", "year"]

# The origin the brain's own entries carry, written by the brain's code. The
# endpoint features send events to refuses it.
BRAIN_ORIGIN = "brain"


@dataclass(frozen=True)
class NewEvent:
    """An event as a caller sends it, already well formed: its subject named
    the way a feature knows it, by a topic's slug or a thing's name."""

    verb: str
    topic: str | None
    entity: str | None
    item: UUID | None
    origin: str
    source: str | None
    run: str | None
    # None when it happened as it was recorded.
    occurred_at: datetime | None
    occurred_precision: Precision
    evidence: str
    payload: dict[str, Any]
    payload_version: int
    idempotency_key: str


@dataclass(frozen=True)
class TopicRef:
    id: UUID
    slug: str
    name: str


@dataclass(frozen=True)
class EntityRef:
    id: UUID
    name: str


@dataclass(frozen=True)
class Event:
    id: UUID
    verb: str
    # The topic as the event named it, an alias included.
    subject_topic: UUID | None
    # The topic it is read on: subject_topic, through its alias if it is one.
    topic: TopicRef | None
    entity: EntityRef | None
    subject_item: UUID | None
    origin: str
    source: str | None
    run: str | None
    occurred_at: datetime
    occurred_precision: Precision
    recorded_at: datetime
    evidence: str
    payload: dict[str, Any]
    payload_version: int
    idempotency_key: str
    # The item this event made, which names it back: it comes first, so it
    # cannot name the item as its subject. None for every other event.
    made_item: UUID | None


# Each event with its subject, the topic read through its alias, and the item
# it made, of which there is at most one (the unique index items_made_by).
_SELECT = """
SELECT e.id, e.verb, e.subject_topic, t.id, t.slug, t.name, n.id, n.name,
       e.subject_item, e.origin, e.source, e.run, e.occurred_at,
       e.occurred_precision, e.recorded_at, e.evidence, e.payload,
       e.payload_version, e.idempotency_key, made.id
FROM events e
LEFT JOIN topics named ON named.id = e.subject_topic
LEFT JOIN topics t ON t.id = coalesce(named.alias_of, named.id)
LEFT JOIN entities n ON n.id = e.subject_entity
LEFT JOIN items made
  ON made.user_id = e.user_id AND made.created_by_event = e.id
"""


def _event(row: TupleRow) -> Event:
    (
        event_id,
        verb,
        subject_topic,
        topic_id,
        slug,
        topic_name,
        entity_id,
        entity_name,
        *rest,
    ) = row
    return Event(
        event_id,
        verb,
        subject_topic,
        TopicRef(topic_id, slug, topic_name) if topic_id is not None else None,
        EntityRef(entity_id, entity_name) if entity_id is not None else None,
        *rest,
    )


async def _by_key(conn: Connection, origin: str, key: str) -> Event | None:
    cursor = await conn.execute(
        f"{_SELECT} WHERE e.origin = %s AND e.idempotency_key = %s", (origin, key)
    )
    row = await cursor.fetchone()
    return _event(row) if row is not None else None


async def _by_id(conn: Connection, event_id: UUID) -> Event:
    cursor = await conn.execute(f"{_SELECT} WHERE e.id = %s", (event_id,))
    row = await cursor.fetchone()
    assert row is not None
    return _event(row)


async def record(
    conn: Connection, new: NewEvent, *, now: datetime
) -> tuple[Event, bool]:
    """Records `new` for the current user at `now`, its recorded time, and
    returns it with True; or, when the user already has an event from that
    origin with that key, returns that one, unchanged, with False.

    Refused when the verb is not one the brain knows, or when it names an
    item the user does not have. A topic or a named thing the user does not
    have is created, a topic as provisional."""
    existing = await _by_key(conn, new.origin, new.idempotency_key)
    if existing is not None:
        return existing, False
    cursor = await conn.execute(
        "SELECT EXISTS (SELECT FROM verbs WHERE verb = %s)", (new.verb,)
    )
    known = await cursor.fetchone()
    if known is None or not known[0]:
        raise Refused(f"{new.verb} is not a verb the brain knows")
    if new.item is not None:
        # Row level security hides another user's, so it is not there either.
        cursor = await conn.execute(
            "SELECT EXISTS (SELECT FROM items WHERE id = %s)", (new.item,)
        )
        found = await cursor.fetchone()
        if found is None or not found[0]:
            raise Refused("there is no such item")
    topic_id = None
    if new.topic is not None:
        topic_id = (await topics.named(conn, new.topic, now=now)).id
    entity_id = None
    if new.entity is not None:
        entity_id = (await entities.named(conn, new.entity, now=now)).id
    cursor = await conn.execute(
        "INSERT INTO events (id, user_id, verb, subject_topic, subject_entity,"
        " subject_item, origin, source, run, occurred_at, occurred_precision,"
        " recorded_at, evidence, payload, payload_version, idempotency_key)"
        f" VALUES (%s, {CURRENT_USER_SQL}, %s, %s, %s, %s, %s, %s, %s, %s, %s,"
        " %s, %s, %s, %s, %s)"
        " ON CONFLICT (user_id, origin, idempotency_key) DO NOTHING RETURNING id",
        (
            uuid7(),
            new.verb,
            topic_id,
            entity_id,
            new.item,
            new.origin,
            new.source,
            new.run,
            new.occurred_at or now,
            new.occurred_precision,
            now,
            new.evidence,
            Jsonb(new.payload),
            new.payload_version,
            new.idempotency_key,
        ),
    )
    inserted = await cursor.fetchone()
    if inserted is None:
        # The same event, sent at the same time, was recorded first.
        first = await _by_key(conn, new.origin, new.idempotency_key)
        assert first is not None
        return first, False
    return await _by_id(conn, inserted[0]), True


async def timeline(
    conn: Connection,
    *,
    before: datetime | None,
    before_id: UUID | None = None,
    limit: int,
) -> list[Event]:
    """The current user's events, newest recorded first and the id breaking
    a tie: at most `limit`, and only those that come after the place
    `before` and `before_id` name when they are given, so the next page
    starts after the last one shown. Events recorded together share a time,
    as an edit's two do, so `before` alone is every event recorded before
    it, and with `before_id` those recorded at it with a smaller id too."""
    cursor = await conn.execute(
        f"{_SELECT} WHERE %(before)s::timestamptz IS NULL"
        " OR e.recorded_at < %(before)s::timestamptz"
        " OR (e.recorded_at = %(before)s::timestamptz"
        " AND e.id < %(before_id)s::uuid)"
        " ORDER BY e.recorded_at DESC, e.id DESC LIMIT %(limit)s",
        {"before": before, "before_id": before_id, "limit": limit},
    )
    return [_event(row) for row in await cursor.fetchall()]
