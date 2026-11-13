"""The memory store: items, what each is about, and the events behind it. See
"The tables", "Which kind goes in which store" and "Versions" in
docs/data-model.md, and "Authority follows the store, not the source" in
docs/trust-model.md.

An item is a claim or a record in one store, decided by its kind. It is never
changed in place: the service may insert items and read them, and change only
whether one is active and why. Every item is made by an event, recorded in the
same transaction through `brain.events.record`, the path the events endpoint
takes, so "why do you believe this" is answered by the same rows that made it.

Every function takes a connection from `brain.db.transaction`, and acts for
that transaction's user.
"""

from dataclasses import dataclass
from datetime import datetime
from typing import Any, Literal, get_args
from uuid import UUID, uuid7

from psycopg import AsyncConnection
from psycopg.rows import TupleRow
from psycopg.types.json import Jsonb

from brain import entities, events, topics
from brain.db import CURRENT_USER_SQL
from brain.errors import Refused

type Connection = AsyncConnection[TupleRow]

type Kind = Literal[
    "profile", "rule", "topic_level", "topic_focus", "state", "history", "decision"
]
type Store = Literal[
    "profile", "procedural", "knowledge", "semantic", "reported_history"
]
type Nature = Literal["claim", "record"]
type Status = Literal["active", "inactive"]
Category = Literal[
    "identity", "goals", "values", "interests", "preferences", "working_style"
]

# A profile entry's categories, "Which kind goes in which store". The
# migration checks the same list.
CATEGORIES: tuple[str, ...] = get_args(Category)

# Where each kind lives and what it is, the table in "Which kind goes in which
# store". The migration checks the same pairs.
PLACES: dict[str, tuple[Store, Nature]] = {
    "profile": ("profile", "claim"),
    "topic_focus": ("profile", "claim"),
    "rule": ("procedural", "claim"),
    "topic_level": ("knowledge", "claim"),
    "state": ("semantic", "claim"),
    "history": ("reported_history", "record"),
    "decision": ("semantic", "claim"),
}

# The kinds that are global by construction, so they take no subject.
GLOBAL_KINDS = frozenset({"rule"})

# The source of an entry the owner typed into the brain.
MANUAL_ENTRY = "manual-entry"
# The longest evidence a hand made entry's event carries, so the timeline
# shows a sentence rather than the whole entry, which the item holds.
EVIDENCE_MAX = 500


@dataclass(frozen=True)
class NewItem:
    """An entry as the owner makes it, already well formed: its subjects
    named the way a person knows them, topics by slug and things by name."""

    kind: str
    content: str
    category: str | None
    topics: tuple[str, ...]
    entities: tuple[str, ...]
    # When it became true, for its event; None for when it was entered.
    occurred_at: datetime | None
    occurred_precision: events.Precision


def evidence_for(kind: str, content: str) -> str:
    """The event's evidence for a hand made entry: a sentence that reads
    without the item, cut to `EVIDENCE_MAX` characters, and then still a
    quotation, ending in an ellipsis and the closing mark."""
    sentence = f'Entered by hand as {kind}: "{content}"'
    if len(sentence) <= EVIDENCE_MAX:
        return sentence
    return sentence[: EVIDENCE_MAX - 2] + '…"'


async def _named_topics(conn: Connection, slugs: tuple[str, ...]) -> list[UUID]:
    """The topics `slugs` name, as they are, aliases included. A hand made
    entry names a topic the user has: an unknown one is refused rather than
    created provisional, as an event's would be."""
    found = []
    for slug in dict.fromkeys(slugs):
        topic = await topics.by_slug(conn, slug)
        if topic is None:
            raise Refused(f"there is no topic {slug}, create it first")
        found.append(topic.id)
    return found


async def _named_entities(
    conn: Connection, names: tuple[str, ...], *, now: datetime
) -> list[UUID]:
    """The named things `names` name, in any letter case, each created when
    the user has none by that name."""
    found: dict[UUID, None] = {}
    for name in names:
        found[(await entities.named(conn, name, now=now)).id] = None
    return list(found)


async def _link_evidence(
    conn: Connection, item_id: UUID, event_id: UUID, *, now: datetime
) -> None:
    await conn.execute(
        "INSERT INTO item_evidence (id, user_id, item_id, event_id, kind, created_at)"
        f" VALUES (%s, {CURRENT_USER_SQL}, %s, %s, 'supports', %s)",
        (uuid7(), item_id, event_id, now),
    )


async def write(conn: Connection, new: NewItem, *, now: datetime) -> UUID:
    """Writes `new` for the current user at `now`, with the `asserted` event
    that proves it was said, and returns the item's id. The event goes first,
    through the same path as the events endpoint, with origin brain; then the
    item, made by it; then its subjects; then the event as its supporting
    evidence. All of it is in the caller's transaction, so a failure at any
    step leaves none of it.

    A new item is active, asserted, and the first of its lineage. Refused
    when a global kind names a subject, or a topic the user does not have
    is named."""
    if new.kind not in PLACES:
        raise Refused(f"{new.kind} is not a kind of item")
    if new.kind in GLOBAL_KINDS and (new.topics or new.entities):
        raise Refused(f"a {new.kind} is global, it takes no topic or named thing")
    if (new.kind == "profile") != (new.category is not None):
        raise Refused("a profile entry, and only one, has a category")
    if new.category is not None and new.category not in CATEGORIES:
        raise Refused(f"{new.category} is not a profile category")
    store, nature = PLACES[new.kind]
    topic_ids = await _named_topics(conn, new.topics)
    entity_ids = await _named_entities(conn, new.entities, now=now)
    item_id = uuid7()
    event, _ = await events.record(
        conn,
        events.NewEvent(
            verb="asserted",
            topic=None,
            entity=None,
            # The event comes first, so it cannot name the item it makes; its
            # payload does, and the item names it back.
            item=None,
            origin=events.BRAIN_ORIGIN,
            source=MANUAL_ENTRY,
            run=None,
            occurred_at=new.occurred_at,
            occurred_precision=new.occurred_precision,
            evidence=evidence_for(new.kind, new.content),
            payload=_payload(item_id, new),
            payload_version=1,
            idempotency_key=f"item:{item_id}",
        ),
        now=now,
    )
    await conn.execute(
        "INSERT INTO items (id, user_id, lineage_id, kind, store, nature,"
        " provenance, content, value, category, details, occurred_at,"
        " occurred_precision, status, inactive_reason, created_at,"
        " created_by_event)"
        f" VALUES (%s, {CURRENT_USER_SQL}, %s, %s, %s, %s, 'asserted', %s, NULL,"
        " %s, %s, NULL, NULL, 'active', NULL, %s, %s)",
        (
            item_id,
            item_id,
            new.kind,
            store,
            nature,
            new.content,
            new.category,
            Jsonb({}),
            now,
            event.id,
        ),
    )
    for topic_id in topic_ids:
        await conn.execute(
            "INSERT INTO item_subjects (item_id, user_id, topic_id, entity_id)"
            f" VALUES (%s, {CURRENT_USER_SQL}, %s, NULL)",
            (item_id, topic_id),
        )
    for entity_id in entity_ids:
        await conn.execute(
            "INSERT INTO item_subjects (item_id, user_id, topic_id, entity_id)"
            f" VALUES (%s, {CURRENT_USER_SQL}, NULL, %s)",
            (item_id, entity_id),
        )
    await _link_evidence(conn, item_id, event.id, now=now)
    return item_id


def _payload(item_id: UUID, new: NewItem) -> dict[str, Any]:
    """What the event carries besides its evidence: which item it made, and
    as what, so the timeline can lead to it."""
    payload: dict[str, Any] = {"item": str(item_id), "kind": new.kind}
    if new.category is not None:
        payload["category"] = new.category
    return payload
