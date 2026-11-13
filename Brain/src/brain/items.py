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

from dataclasses import dataclass, field, replace
from datetime import datetime
from typing import Any, Literal, get_args
from uuid import UUID, uuid7

from psycopg import AsyncConnection
from psycopg.rows import TupleRow
from psycopg.types.json import Jsonb

from brain import entities, events, topics
from brain.db import CURRENT_USER_SQL, USER_SETTING
from brain.errors import Conflict, NotFound, Refused

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
CATEGORIES: tuple[Category, ...] = get_args(Category)

# How well the owner knows a topic, and what he is doing about it: "How level
# is represented" in docs/knowledge-model.md. A level means: shallow, knows the
# vocabulary; working, can use it with help; solid, can use it unaided and
# explain it; deep, knows the internals and could teach it.
Level = Literal["shallow", "working", "solid", "deep"]
Focus = Literal["learning", "steady", "not_pursuing"]

# The kinds that hold one value about one topic, each with the values it may
# hold, and what its generated content calls it. A topic has at most one
# active entry of each.
VALUES: dict[str, tuple[str, ...]] = {
    "topic_level": get_args(Level),
    "topic_focus": get_args(Focus),
}
VALUE_LABELS = {"topic_level": "Level", "topic_focus": "Focus"}

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

# The kinds that say when they happened, on the item itself: a record, and a
# decision, which was made at a time. The migration allows no other.
DATED_KINDS = frozenset({"history", "decision"})

# The verb of the event that makes an entry by hand, where it is not
# `asserted`: a decision is written with a `decided` event, the permanent
# record that it was made, see "Decisions" in docs/event-model.md.
MADE_BY = {"decision": "decided"}

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
    # A level or a focus, for the kinds in VALUES alone.
    value: str | None = None
    details: dict[str, Any] = field(default_factory=dict)


# How much of each text an edit's evidence quotes, the old and the new.
EDIT_QUOTE_MAX = 200


def _quoted(said: str, content: str) -> str:
    """`said` and then `content` in quotation marks, a sentence that reads
    without the item, cut to `EVIDENCE_MAX` characters, and then still a
    quotation, ending in an ellipsis and the closing mark."""
    sentence = f'{said}: "{content}"'
    if len(sentence) <= EVIDENCE_MAX:
        return sentence
    return sentence[: EVIDENCE_MAX - 2] + '…"'


def evidence_for(kind: str, content: str) -> str:
    """The event's evidence for a hand made entry."""
    return _quoted(f"Entered by hand as {kind}", content)


def _cut(text: str, limit: int) -> str:
    """`text`, or its first characters and an ellipsis, `limit` in all."""
    return text if len(text) <= limit else text[: limit - 1] + "…"


def edit_evidence(old: str, new: str) -> str:
    """The `edited` event's evidence: both texts, each cut to
    `EDIT_QUOTE_MAX` characters, so the judgment reads without the items."""
    return (
        f'Corrected by hand from "{_cut(old, EDIT_QUOTE_MAX)}"'
        f' to "{_cut(new, EDIT_QUOTE_MAX)}"'
    )


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
    conn: Connection,
    item_id: UUID,
    event_id: UUID,
    *,
    now: datetime,
    kind: Literal["supports", "contradicts"] = "supports",
) -> None:
    await conn.execute(
        "INSERT INTO item_evidence (id, user_id, item_id, event_id, kind, created_at)"
        f" VALUES (%s, {CURRENT_USER_SQL}, %s, %s, %s, %s)",
        (uuid7(), item_id, event_id, kind, now),
    )


async def _link_judgment(
    conn: Connection, item_id: UUID, event: events.Event, *, now: datetime
) -> None:
    """The owner's judgment of an item as evidence on it, for it or against
    it as its verb's polarity says: `edited` and `retired` against, `restored`
    for. It is what keeps consolidation from proposing again what the owner
    corrected, see "Editing changes provenance" in docs/trust-model.md. A
    neutral verb, such as `rated`, says neither, and is refused rather than
    read as one of them."""
    cursor = await conn.execute(
        "SELECT polarity FROM verbs WHERE verb = %s", (event.verb,)
    )
    row = await cursor.fetchone()
    assert row is not None
    if row[0] == 0:
        raise ValueError(f"{event.verb} is neutral, it is no evidence for or against")
    kind: Literal["supports", "contradicts"] = (
        "supports" if row[0] > 0 else "contradicts"
    )
    await _link_evidence(conn, item_id, event.id, now=now, kind=kind)


async def _link_relation(
    conn: Connection,
    from_item: UUID,
    to_item: UUID,
    kind: str,
    event_id: UUID,
    *,
    now: datetime,
) -> None:
    await conn.execute(
        "INSERT INTO item_relations (id, user_id, from_item, to_item, kind,"
        " created_at, ended_at, created_by_event)"
        f" VALUES (%s, {CURRENT_USER_SQL}, %s, %s, %s, %s, NULL, %s)",
        (uuid7(), from_item, to_item, kind, now, event_id),
    )


async def _insert(
    conn: Connection,
    item_id: UUID,
    *,
    lineage_id: UUID,
    kind: str,
    content: str,
    value: str | None,
    category: str | None,
    details: dict[str, Any],
    occurred_at: datetime | None,
    occurred_precision: events.Precision | None,
    topic_ids: list[UUID],
    entity_ids: list[UUID],
    event_id: UUID,
    now: datetime,
) -> None:
    """Inserts one item, active and asserted, made by `event_id`, and what it
    is about. Its store and nature follow from its kind."""
    store, nature = PLACES[kind]
    await conn.execute(
        "INSERT INTO items (id, user_id, lineage_id, kind, store, nature,"
        " provenance, content, value, category, details, occurred_at,"
        " occurred_precision, status, inactive_reason, created_at,"
        " created_by_event)"
        f" VALUES (%s, {CURRENT_USER_SQL}, %s, %s, %s, %s, 'asserted', %s, %s,"
        " %s, %s, %s, %s, 'active', NULL, %s, %s)",
        (
            item_id,
            lineage_id,
            kind,
            store,
            nature,
            content,
            value,
            category,
            Jsonb(details),
            occurred_at,
            occurred_precision,
            now,
            event_id,
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


async def write(conn: Connection, new: NewItem, *, now: datetime) -> UUID:
    """Writes `new` for the current user at `now`, with the `asserted` event
    that proves it was said, or for a decision the `decided` event, and returns the item's id. The event goes first,
    through the same path as the events endpoint, with origin brain; then the
    item, made by it; then its subjects; then the event as its supporting
    evidence. All of it is in the caller's transaction, so a failure at any
    step leaves none of it.

    A new item is active, asserted, and the first of its lineage. Refused
    when a global kind names a subject, or a topic the user does not have
    is named. A level or a focus is set with `set_on_topic`, which keeps one
    active per topic, and never written here."""
    if new.kind in VALUES:
        raise ValueError(f"a {new.kind} is set with set_on_topic")
    return await _write(conn, new, now=now)


async def _write(conn: Connection, new: NewItem, *, now: datetime) -> UUID:
    if new.kind not in PLACES:
        raise Refused(f"{new.kind} is not a kind of item")
    if new.kind in GLOBAL_KINDS and (new.topics or new.entities):
        raise Refused(f"a {new.kind} is global, it takes no topic or named thing")
    if (new.kind == "profile") != (new.category is not None):
        raise Refused("a profile entry, and only one, has a category")
    if new.category is not None and new.category not in CATEGORIES:
        raise Refused(f"{new.category} is not a profile category")
    topic_ids = await _named_topics(conn, new.topics)
    entity_ids = await _named_entities(conn, new.entities, now=now)
    item_id = uuid7()
    event, _ = await events.record(
        conn,
        events.NewEvent(
            verb=MADE_BY.get(new.kind, "asserted"),
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
            payload=_payload(item_id, new.kind, new.category),
            payload_version=1,
            idempotency_key=f"item:{item_id}",
        ),
        now=now,
    )
    # A record says when it happened, and a decision when it was made: when
    # it was entered, unless told.
    dated = new.kind in DATED_KINDS
    await _insert(
        conn,
        item_id,
        lineage_id=item_id,
        kind=new.kind,
        content=new.content,
        value=new.value,
        category=new.category,
        details=new.details,
        occurred_at=(new.occurred_at or now) if dated else None,
        occurred_precision=new.occurred_precision if dated else None,
        topic_ids=topic_ids,
        entity_ids=entity_ids,
        event_id=event.id,
        now=now,
    )
    await _link_evidence(conn, item_id, event.id, now=now)
    return item_id


def _payload(item_id: UUID, kind: str, category: str | None) -> dict[str, Any]:
    """What an asserted event carries besides its evidence: which item it
    made, and as what, so the timeline can lead to it."""
    payload: dict[str, Any] = {"item": str(item_id), "kind": kind}
    if category is not None:
        payload["category"] = category
    return payload


@dataclass(frozen=True)
class Item:
    id: UUID
    lineage_id: UUID
    kind: Kind
    store: Store
    nature: Nature
    provenance: Literal["asserted", "derived"]
    content: str
    value: str | None
    category: str | None
    details: dict[str, Any]
    occurred_at: datetime | None
    occurred_precision: events.Precision | None
    status: Status
    inactive_reason: Literal["superseded", "retired"] | None
    created_at: datetime
    created_by_event: UUID
    # What it is about, each topic read through its alias, by slug; and each
    # named thing, by name. Both empty for a global item.
    topics: tuple[events.TopicRef, ...] = ()
    entities: tuple[events.EntityRef, ...] = ()


@dataclass(frozen=True)
class EvidenceEvent:
    id: UUID
    verb: str
    origin: str
    source: str | None
    evidence: str
    occurred_at: datetime
    occurred_precision: events.Precision
    recorded_at: datetime


@dataclass(frozen=True)
class Evidence:
    id: UUID
    kind: Literal["supports", "contradicts"]
    created_at: datetime
    event: EvidenceEvent


@dataclass(frozen=True)
class Relation:
    id: UUID
    from_item: UUID
    to_item: UUID
    kind: str
    created_at: datetime
    ended_at: datetime | None
    created_by_event: UUID


# A relation's columns, in the order `Relation` takes them.
RELATION_COLUMNS = (
    "id, from_item, to_item, kind, created_at, ended_at, created_by_event"
)


@dataclass(frozen=True)
class Detail:
    """One item as a single read answers it: the item, the events behind it,
    oldest first, which are what confidence and currency would be computed
    from, and every relation it is on either end of."""

    item: Item
    evidence: tuple[Evidence, ...]
    relations: tuple[Relation, ...]


# An item's columns, in the order `Item` takes them.
FIELDS = (
    "id",
    "lineage_id",
    "kind",
    "store",
    "nature",
    "provenance",
    "content",
    "value",
    "category",
    "details",
    "occurred_at",
    "occurred_precision",
    "status",
    "inactive_reason",
    "created_at",
    "created_by_event",
)
COLUMNS = ", ".join(FIELDS)

type Subjects = tuple[tuple[events.TopicRef, ...], tuple[events.EntityRef, ...]]


async def _subjects(conn: Connection, item_ids: list[UUID]) -> dict[UUID, Subjects]:
    """Each item's subjects: its topics read through their aliases, one
    each however often they were named, by slug; its named things by name,
    without letter case."""
    found_topics: dict[UUID, dict[UUID, events.TopicRef]] = {i: {} for i in item_ids}
    found_entities: dict[UUID, list[events.EntityRef]] = {i: [] for i in item_ids}
    cursor = await conn.execute(
        "SELECT s.item_id, t.id, t.slug, t.name FROM item_subjects s"
        " JOIN topics named ON named.id = s.topic_id"
        " JOIN topics t ON t.id = coalesce(named.alias_of, named.id)"
        " WHERE s.item_id = ANY(%s)",
        (item_ids,),
    )
    for item_id, topic_id, slug, name in await cursor.fetchall():
        found_topics[item_id][topic_id] = events.TopicRef(topic_id, slug, name)
    cursor = await conn.execute(
        "SELECT s.item_id, n.id, n.name FROM item_subjects s"
        " JOIN entities n ON n.id = s.entity_id WHERE s.item_id = ANY(%s)",
        (item_ids,),
    )
    for item_id, entity_id, name in await cursor.fetchall():
        found_entities[item_id].append(events.EntityRef(entity_id, name))
    return {
        item_id: (
            tuple(sorted(found_topics[item_id].values(), key=lambda t: t.slug)),
            tuple(
                sorted(found_entities[item_id], key=lambda n: (n.name.lower(), n.id))
            ),
        )
        for item_id in item_ids
    }


async def from_rows(conn: Connection, rows: list[TupleRow]) -> list[Item]:
    """The items `rows` hold, each a row whose first columns are `COLUMNS`,
    in that order, with their subjects. Columns after those are left to the
    caller, which reads them from the same rows."""
    subjects = await _subjects(conn, [row[0] for row in rows])
    found = []
    for row in rows:
        named_topics, named_entities = subjects[row[0]]
        item = Item(*row[: len(FIELDS)])
        found.append(replace(item, topics=named_topics, entities=named_entities))
    return found


async def get(conn: Connection, item_id: UUID) -> Detail | None:
    """The current user's item with this id, with its evidence and
    relations, or None when they have none."""
    item = await _one(conn, item_id)
    if item is None:
        return None
    cursor = await conn.execute(
        "SELECT ev.id, ev.kind, ev.created_at, e.id, e.verb, e.origin, e.source,"
        " e.evidence, e.occurred_at, e.occurred_precision, e.recorded_at"
        " FROM item_evidence ev JOIN events e ON e.id = ev.event_id"
        " WHERE ev.item_id = %s ORDER BY e.recorded_at, ev.id",
        (item_id,),
    )
    evidence = tuple(
        Evidence(evidence_id, kind, created_at, EvidenceEvent(*event))
        for evidence_id, kind, created_at, *event in await cursor.fetchall()
    )
    cursor = await conn.execute(
        f"SELECT {RELATION_COLUMNS}"
        " FROM item_relations WHERE from_item = %(id)s OR to_item = %(id)s"
        " ORDER BY created_at, id",
        {"id": item_id},
    )
    relations = tuple(Relation(*row) for row in await cursor.fetchall())
    return Detail(item, evidence, relations)


# The item `i` is about the topic `%(topic)s`, a slug, and the named thing
# `%(entity)s`, a name, each when it is not null: a topic through aliases on
# either side, and only itself, a name without letter case. A condition for
# a query's WHERE, as `listing` reads them.
SUBJECT_FILTERS = (
    "(%(topic)s::text IS NULL OR EXISTS ("
    "  SELECT FROM item_subjects s"
    "  JOIN topics named ON named.id = s.topic_id"
    "  JOIN topics wanted ON wanted.slug = %(topic)s"
    "  WHERE s.item_id = i.id"
    "  AND coalesce(named.alias_of, named.id)"
    "   = coalesce(wanted.alias_of, wanted.id)))"
    " AND (%(entity)s::text IS NULL OR EXISTS ("
    "  SELECT FROM item_subjects s JOIN entities n ON n.id = s.entity_id"
    "  WHERE s.item_id = i.id AND lower(n.name) = lower(%(entity)s)))"
)


async def listing(
    conn: Connection,
    *,
    kind: str | None,
    status: str | None,
    topic: str | None,
    entity: str | None,
) -> list[Item]:
    """The current user's items, newest first, narrowed by each filter that
    is given: the kind, the status, a topic by slug and a named thing by
    name. A topic matches through aliases, on either side: an item about an
    alias is about its target, and asking for an alias asks for the target.
    Only the topic itself matches, not those under it. A name matches
    without letter case. A subject the user does not have matches nothing."""
    cursor = await conn.execute(
        f"SELECT {COLUMNS} FROM items i"
        " WHERE (%(kind)s::text IS NULL OR i.kind = %(kind)s)"
        " AND (%(status)s::text IS NULL OR i.status = %(status)s)"
        f" AND {SUBJECT_FILTERS}"
        " ORDER BY i.created_at DESC, i.id DESC",
        {"kind": kind, "status": status, "topic": topic, "entity": entity},
    )
    return await from_rows(conn, await cursor.fetchall())


async def _one(conn: Connection, item_id: UUID) -> Item | None:
    cursor = await conn.execute(
        f"SELECT {COLUMNS} FROM items WHERE id = %s", (item_id,)
    )
    row = await cursor.fetchone()
    if row is None:
        return None
    [item] = await from_rows(conn, [row])
    return item


async def _set_status(
    conn: Connection,
    item_id: UUID,
    *,
    was: tuple[Status, str | None],
    to: tuple[Status, str | None],
) -> bool:
    """Moves the item from status and reason `was` to `to`, and says whether
    it did. One conditional statement: of two requests changing the same item
    at once, the second waits for the first and then finds it no longer as it
    was, so it changes nothing."""
    cursor = await conn.execute(
        "UPDATE items SET status = %s, inactive_reason = %s"
        " WHERE id = %s AND status = %s"
        " AND inactive_reason IS NOT DISTINCT FROM %s RETURNING id",
        (*to, item_id, *was),
    )
    return await cursor.fetchone() is not None


def _judgment(
    verb: str, item_id: UUID, evidence: str, key: str, payload: dict[str, Any]
) -> events.NewEvent:
    """What the owner did to an item, as an event against it."""
    return events.NewEvent(
        verb=verb,
        topic=None,
        entity=None,
        item=item_id,
        origin=events.BRAIN_ORIGIN,
        source=MANUAL_ENTRY,
        run=None,
        occurred_at=None,
        occurred_precision="instant",
        evidence=evidence,
        payload=payload,
        payload_version=1,
        idempotency_key=key,
    )


@dataclass(frozen=True)
class Edit:
    """A correction as the owner makes it: each field None to keep what the
    item has, and the kind, which an edit never changes, to say what it is
    meant to correct. Subjects are named as for a new item."""

    kind: str
    content: str | None
    category: str | None
    topics: tuple[str, ...] | None
    entities: tuple[str, ...] | None
    # A level or a focus, for the kinds in VALUES alone.
    value: str | None = None


async def _canonical(conn: Connection, topic_ids: list[UUID]) -> set[UUID]:
    """The topics these ids are read as, each through its alias."""
    cursor = await conn.execute(
        "SELECT coalesce(alias_of, id) FROM topics WHERE id = ANY(%s)", (topic_ids,)
    )
    return {row[0] for row in await cursor.fetchall()}


async def _named_subjects(
    conn: Connection, item_id: UUID
) -> tuple[list[UUID], list[UUID]]:
    """The item's topics as they were named, aliases included, and its named
    things, as stored."""
    cursor = await conn.execute(
        "SELECT topic_id, entity_id FROM item_subjects WHERE item_id = %s",
        (item_id,),
    )
    rows = await cursor.fetchall()
    return (
        [topic for topic, _ in rows if topic is not None],
        [entity for _, entity in rows if entity is not None],
    )


async def edit(conn: Connection, item_id: UUID, change: Edit, *, now: datetime) -> UUID:
    """Corrects the current user's item `item_id` at `now`, and returns the
    new version's id. Nothing is changed in place, see "Versions" in
    docs/data-model.md: the new version is inserted with the same lineage,
    active and asserted whatever the old one was, holding what the edit
    gives and the old version's everything else, its subjects included; the
    old one becomes inactive, superseded, and a `supersedes` relation links
    the new to it. An `edited` event against the old version records the
    judgment and is evidence against it; an `asserted` event makes the new
    one and supports it. All of it is in the caller's transaction.

    Not found for an item the user does not have. Refused for a kind that is
    not the item's, a field the kind does not take, a topic the user does not
    have, or an edit that changes nothing. A conflict when the item is not
    the newest version of its entry, or is retired: it is restored first."""
    old = await _one(conn, item_id)
    if old is None:
        raise NotFound
    if change.kind != old.kind:
        raise Refused(f"the item is a {old.kind}, not a {change.kind}")
    if old.inactive_reason == "superseded":
        raise Conflict("only the newest version of an entry can be edited")
    if old.inactive_reason == "retired":
        raise Conflict("a retired item is restored before it is edited")
    if old.kind in GLOBAL_KINDS and (change.topics or change.entities):
        raise Refused(f"a {old.kind} is global, it takes no topic or named thing")
    if change.category is not None:
        if old.kind != "profile":
            raise Refused("only a profile entry has a category")
        if change.category not in CATEGORIES:
            raise Refused(f"{change.category} is not a profile category")
    if change.value is not None:
        if old.kind not in VALUES:
            raise Refused(f"a {old.kind} holds no value")
        if change.value not in VALUES[old.kind]:
            raise Refused(f"{change.value} is not a {old.kind}")
    content = change.content if change.content is not None else old.content
    category = change.category if change.category is not None else old.category
    value = change.value if change.value is not None else old.value
    old_topics, old_entities = await _named_subjects(conn, item_id)
    topic_ids = (
        await _named_topics(conn, change.topics)
        if change.topics is not None
        else old_topics
    )
    entity_ids = (
        await _named_entities(conn, change.entities, now=now)
        if change.entities is not None
        else old_entities
    )
    if (
        content == old.content
        and category == old.category
        and value == old.value
        and await _canonical(conn, topic_ids) == await _canonical(conn, old_topics)
        and set(entity_ids) == set(old_entities)
    ):
        raise Refused("the edit changes nothing")
    if not await _set_status(
        conn, item_id, was=("active", None), to=("inactive", "superseded")
    ):
        raise Conflict("the item changed while it was being edited")
    new_id = uuid7()
    edited, _ = await events.record(
        conn,
        _judgment(
            "edited",
            item_id,
            edit_evidence(old.content, content),
            f"edited:{new_id}",
            {"item": str(item_id), "version": str(new_id)},
        ),
        now=now,
    )
    asserted, _ = await events.record(
        conn,
        events.NewEvent(
            verb="asserted",
            topic=None,
            entity=None,
            item=None,
            origin=events.BRAIN_ORIGIN,
            source=MANUAL_ENTRY,
            run=None,
            occurred_at=None,
            occurred_precision="instant",
            evidence=evidence_for(old.kind, content),
            payload={
                **_payload(new_id, old.kind, category),
                "supersedes": str(item_id),
            },
            payload_version=1,
            idempotency_key=f"item:{new_id}",
        ),
        now=now,
    )
    await _insert(
        conn,
        new_id,
        lineage_id=old.lineage_id,
        kind=old.kind,
        content=content,
        value=value,
        category=category,
        details=old.details,
        occurred_at=old.occurred_at,
        occurred_precision=old.occurred_precision,
        topic_ids=list(dict.fromkeys(topic_ids)),
        entity_ids=list(dict.fromkeys(entity_ids)),
        event_id=asserted.id,
        now=now,
    )
    await _link_evidence(conn, new_id, asserted.id, now=now)
    await _link_judgment(conn, item_id, edited, now=now)
    await _link_relation(conn, new_id, item_id, "supersedes", edited.id, now=now)
    return new_id


async def retire(conn: Connection, item_id: UUID, *, now: datetime) -> None:
    """Puts the current user's item away at `now`: it becomes inactive,
    retired, and a `retired` event against it records the judgment and is
    evidence against it. It stays readable and can be restored. Only the
    owner retires, see "Nothing is destroyed, and only the owner retires" in
    docs/trust-model.md: nothing calls this for an item's age or a score.

    Not found for an item the user does not have. A conflict for one already
    retired, or a superseded version, which is not the entry any more."""
    item = await _one(conn, item_id)
    if item is None:
        raise NotFound
    if item.inactive_reason == "superseded":
        raise Conflict("a superseded version is not retired, its newest version is")
    if item.inactive_reason == "retired":
        raise Conflict("the item is already retired")
    if not await _set_status(
        conn, item_id, was=("active", None), to=("inactive", "retired")
    ):
        raise Conflict("the item changed while it was being retired")
    event, _ = await events.record(
        conn,
        _judgment(
            "retired",
            item_id,
            _quoted("Retired by hand", item.content),
            f"retired:{uuid7()}",
            {"item": str(item_id)},
        ),
        now=now,
    )
    await _link_judgment(conn, item_id, event, now=now)


async def restore(conn: Connection, item_id: UUID, *, now: datetime) -> None:
    """Brings the current user's retired item back at `now`: it is active
    again, and a `restored` event against it records the judgment and
    supports it.

    Not found for an item the user does not have. A conflict for one that is
    not retired: an active item has nothing to come back from, and a
    superseded version stays in its entry's history, behind its newest."""
    item = await _one(conn, item_id)
    if item is None:
        raise NotFound
    if item.inactive_reason == "superseded":
        raise Conflict("only a retired item is restored, not a superseded version")
    if item.status == "active":
        raise Conflict("the item is active, there is nothing to restore")
    if item.kind in VALUES:
        [topic] = item.topics
        await _lock_topic(conn, item.kind, topic.id)
        if await _active_on_topic(conn, item.kind, topic.id) is not None:
            raise Conflict(
                f"{topic.slug} has an active {item.kind} already, retire it first"
            )
    if not await _set_status(
        conn, item_id, was=("inactive", "retired"), to=("active", None)
    ):
        raise Conflict("the item changed while it was being restored")
    event, _ = await events.record(
        conn,
        _judgment(
            "restored",
            item_id,
            _quoted("Restored by hand", item.content),
            f"restored:{uuid7()}",
            {"item": str(item_id)},
        ),
        now=now,
    )
    await _link_judgment(conn, item_id, event, now=now)


async def _hold_active(conn: Connection, item_ids: list[UUID]) -> bool:
    """Waits for, then holds until the transaction ends, a lock on each of
    these items, taken in id order, and says whether every one is active
    once it has it. A change of status takes the row's lock too, so of a
    change and this at once, whichever comes second sees what the first
    left: an item retired meanwhile is found inactive, and one locked here
    is not retired until this transaction ends."""
    cursor = await conn.execute(
        "SELECT status FROM items WHERE id = ANY(%s) ORDER BY id FOR NO KEY UPDATE",
        (item_ids,),
    )
    found = [row[0] for row in await cursor.fetchall()]
    return len(found) == len(set(item_ids)) and all(s == "active" for s in found)


async def confirm(conn: Connection, item_id: UUID, *, now: datetime) -> None:
    """Records at `now` that the owner says the current user's claim
    `item_id` still holds: a `confirmed` event against it, linked as
    evidence supporting it, which is what moves when it was last supported,
    see "Confidence, currency and status are three different things" in
    docs/trust-model.md. Nothing on the item changes.

    Not found for an item the user does not have. Refused for a record,
    which asserts nothing about now. A conflict for an item that is not
    active: a superseded version, whose newest is the one to confirm, or a
    retired one, which is restored first."""
    item = await _one(conn, item_id)
    if item is None:
        raise NotFound
    if item.nature != "claim":
        raise Refused("a record asserts nothing about now, there is nothing to confirm")
    if item.inactive_reason == "superseded":
        raise Conflict("only the newest version of an entry is confirmed")
    if item.inactive_reason == "retired":
        raise Conflict("a retired item is restored before it is confirmed")
    if not await _hold_active(conn, [item_id]):
        raise Conflict("the item changed while it was being confirmed")
    event, _ = await events.record(
        conn,
        _judgment(
            "confirmed",
            item_id,
            _quoted("Confirmed by hand", item.content),
            f"confirmed:{uuid7()}",
            {"item": str(item_id)},
        ),
        now=now,
    )
    await _link_judgment(conn, item_id, event, now=now)


async def replace_decision(
    conn: Connection, item_id: UUID, by: UUID, *, now: datetime
) -> None:
    """Replaces the current user's decision `item_id` with their newer
    decision `by` at `now`: the old one becomes inactive, superseded, and a
    `supersedes` relation links the new to it, as an edit's new version
    does, though the two are entries of their own. An `edited` event against
    the old one records the judgment, is evidence against it, and makes the
    relation. The old one's `decided` event, the record that it was made, is
    untouched, so both stay readable. All of it is in the caller's
    transaction.

    Not found for an `item_id` the user does not have. Refused for a `by`
    they do not have, for either not being a decision, and for two versions
    of one entry, a decision itself included. A conflict when either is not
    active."""
    old = await _one(conn, item_id)
    if old is None:
        raise NotFound
    new = await _one(conn, by)
    if new is None:
        raise Refused(f"there is no item {by}")
    if old.kind != "decision" or new.kind != "decision":
        raise Refused("a decision is replaced by a decision, and by nothing else")
    if old.lineage_id == new.lineage_id:
        raise Refused("a decision cannot replace itself or a version of itself")
    if old.status != "active" or new.status != "active":
        raise Conflict("both decisions must be active")
    # Both locked, in id order, so of two replacing each other at once the
    # second waits and then finds the first's inactive.
    if not await _hold_active(conn, [item_id, by]) or not await _set_status(
        conn, item_id, was=("active", None), to=("inactive", "superseded")
    ):
        raise Conflict("a decision changed while it was being replaced")
    event, _ = await events.record(
        conn,
        _judgment(
            "edited",
            item_id,
            f'Replaced by hand: "{_cut(old.content, EDIT_QUOTE_MAX)}"'
            f' by "{_cut(new.content, EDIT_QUOTE_MAX)}"',
            f"replaced:{uuid7()}",
            {"item": str(item_id), "replaced_by": str(by)},
        ),
        now=now,
    )
    await _link_judgment(conn, item_id, event, now=now)
    await _link_relation(conn, by, item_id, "supersedes", event.id, now=now)


async def mark_contradicting(
    conn: Connection, item_id: UUID, other: UUID, *, now: datetime
) -> Relation:
    """Marks the current user's claims `item_id` and `other` as contradicting
    each other at `now`, and returns the relation, a `contradicts` from the
    first to the second. A `contradicted` event about the first makes it:
    the owner's judgment that the two disagree, and of neither that it is
    wrong, so it is no evidence on either. Both stay as they are; what an
    answer does with the pair is `brain.retrieval.conflicts`. This is the
    only way a contradiction is made until consolidation exists.

    Not found for an `item_id` the user does not have. Refused for an
    `other` they do not have, the item itself, and either being a record,
    which asserts nothing that could be contradicted. A conflict when either
    is not active, or the two are already marked, either way round."""
    one = await _one(conn, item_id)
    if one is None:
        raise NotFound
    against = await _one(conn, other)
    if against is None:
        raise Refused(f"there is no item {other}")
    if one.id == against.id:
        raise Refused("an item does not contradict itself")
    if one.nature != "claim" or against.nature != "claim":
        raise Refused("only a claim contradicts another, a record asserts nothing")
    if one.status != "active" or against.status != "active":
        raise Conflict("both items must be active")
    # Both locked, in id order, so of two marking the same pair at once the
    # second waits and then finds the first's relation.
    if not await _hold_active(conn, [item_id, other]):
        raise Conflict("an item changed while it was being marked")
    cursor = await conn.execute(
        "SELECT EXISTS (SELECT FROM item_relations WHERE kind = 'contradicts'"
        " AND ended_at IS NULL AND ARRAY[from_item, to_item] @> ARRAY[%s, %s])",
        (item_id, other),
    )
    row = await cursor.fetchone()
    if row is not None and row[0]:
        raise Conflict("the two are already marked as contradicting")
    event, _ = await events.record(
        conn,
        _judgment(
            "contradicted",
            item_id,
            f'Marked by hand as contradicting: "{_cut(one.content, EDIT_QUOTE_MAX)}"'
            f' and "{_cut(against.content, EDIT_QUOTE_MAX)}"',
            f"contradicted:{uuid7()}",
            {"item": str(item_id), "contradicts": str(other)},
        ),
        now=now,
    )
    cursor = await conn.execute(
        "INSERT INTO item_relations (id, user_id, from_item, to_item, kind,"
        " created_at, ended_at, created_by_event)"
        f" VALUES (%s, {CURRENT_USER_SQL}, %s, %s, 'contradicts', %s, NULL, %s)"
        f" RETURNING {RELATION_COLUMNS}",
        (uuid7(), item_id, other, now, event.id),
    )
    made = await cursor.fetchone()
    assert made is not None
    return Relation(*made)


async def end_contradiction(
    conn: Connection, relation_id: UUID, *, now: datetime
) -> Relation:
    """Takes back a contradiction the owner marked: ends the relation at
    `now`, and returns it. The row stays, so what was marked and when stays
    readable, as a topic relation's does. One already ended keeps the time
    it ended.

    Not found for a relation the user does not have. Refused for any other
    kind: a `supersedes` relation is part of an entry's history, and is not
    taken back."""
    cursor = await conn.execute(
        f"SELECT {RELATION_COLUMNS} FROM item_relations WHERE id = %s",
        (relation_id,),
    )
    row = await cursor.fetchone()
    if row is None:
        raise NotFound
    relation = Relation(*row)
    if relation.kind != "contradicts":
        raise Refused(f"only a contradiction is taken back, not {relation.kind}")
    if relation.ended_at is not None:
        return relation
    cursor = await conn.execute(
        "UPDATE item_relations SET ended_at = %s"
        f" WHERE id = %s AND ended_at IS NULL RETURNING {RELATION_COLUMNS}",
        (now, relation_id),
    )
    row = await cursor.fetchone()
    if row is None:
        # Ended meanwhile: answered as it was ended.
        cursor = await conn.execute(
            f"SELECT {RELATION_COLUMNS} FROM item_relations WHERE id = %s",
            (relation_id,),
        )
        row = await cursor.fetchone()
        assert row is not None
    return Relation(*row)


async def versions(conn: Connection, item_id: UUID) -> list[Item] | None:
    """Every version of the entry `item_id` is one of, oldest first, each
    with its subjects: the whole history of one entry, whichever version is
    asked for. None when the user has no such item."""
    cursor = await conn.execute(
        f"SELECT {COLUMNS} FROM items"
        " WHERE lineage_id = (SELECT lineage_id FROM items WHERE id = %s)"
        " ORDER BY created_at, id",
        (item_id,),
    )
    rows = await cursor.fetchall()
    if not rows:
        return None
    return await from_rows(conn, rows)


async def _lock_topic(conn: Connection, kind: str, topic_id: UUID) -> None:
    """Waits for, then holds until the transaction ends, the lock on the
    current user's entries of `kind` on the canonical topic `topic_id`. Every
    change that could make a second active one takes it before it looks, so
    of two at once the second finds what the first wrote. No migration can
    say one active per topic, since the topic is a row of item_subjects."""
    await conn.execute(
        "SELECT pg_advisory_xact_lock(hashtextextended("
        f"'brain.knowledge:' || current_setting('{USER_SETTING}')"
        " || ':' || %s || ':' || %s::text, 0))",
        (kind, topic_id),
    )


async def _active_on_topic(conn: Connection, kind: str, topic_id: UUID) -> UUID | None:
    """The current user's active entry of `kind` on the canonical topic
    `topic_id`, read through aliases, or None. Should two topics that each
    had one be marked the same, the newest is the one that stands."""
    cursor = await conn.execute(
        "SELECT i.id FROM items i WHERE i.kind = %(kind)s AND i.status = 'active'"
        " AND EXISTS ("
        "  SELECT FROM item_subjects s JOIN topics named ON named.id = s.topic_id"
        "  WHERE s.item_id = i.id"
        "  AND coalesce(named.alias_of, named.id) = %(topic)s)"
        " ORDER BY i.created_at DESC, i.id DESC LIMIT 1",
        {"kind": kind, "topic": topic_id},
    )
    row = await cursor.fetchone()
    return row[0] if row is not None else None


async def _canonical_topic(conn: Connection, slug: str) -> topics.Topic:
    """The topic `slug` stands for, its target when it is an alias. Refused
    when the user has none by that slug: an entry by hand never makes one."""
    named = await topics.by_slug(conn, slug)
    if named is None:
        raise Refused(f"there is no topic {slug}, create it first")
    if named.alias_of is None:
        return named
    target = await topics.get(conn, named.alias_of)
    assert target is not None
    return target


async def set_on_topic(
    conn: Connection, kind: str, slug: str, value: str, *, now: datetime
) -> UUID:
    """Sets the current user's level or focus on the topic `slug` stands for
    to `value` at `now`, and returns the entry's id: a new one when the topic
    has no active entry of `kind`, and otherwise the new version `edit` makes
    of the one it has, so a topic keeps one active level and one active focus
    and the versions of that entry are the history of its values. On an
    alias, it is set on the alias's target, and names it. Its content is
    generated, `<Level|Focus> for <topic name>: <value>`.

    Refused for a value the kind does not hold, a topic the user does not
    have, or the value the topic already has."""
    if kind not in VALUES:
        raise ValueError(f"{kind} holds no value")
    if value not in VALUES[kind]:
        raise Refused(f"{value} is not a {kind}")
    topic = await _canonical_topic(conn, slug)
    await _lock_topic(conn, kind, topic.id)
    content = f"{VALUE_LABELS[kind]} for {topic.name}: {value}"
    current = await _active_on_topic(conn, kind, topic.id)
    if current is not None:
        change = Edit(
            kind=kind,
            content=content,
            category=None,
            topics=None,
            entities=None,
            value=value,
        )
        return await edit(conn, current, change, now=now)
    return await _write(
        conn,
        NewItem(
            kind=kind,
            content=content,
            category=None,
            topics=(topic.slug,),
            entities=(),
            occurred_at=None,
            occurred_precision="instant",
            value=value,
        ),
        now=now,
    )


async def write_misconception(
    conn: Connection,
    slug: str,
    believed: str,
    correct: str,
    *,
    occurred_at: datetime | None,
    occurred_precision: events.Precision,
    now: datetime,
) -> UUID:
    """Records, for the current user at `now`, that they believed something
    about the topic `slug` that is not so, and returns the entry's id. A
    corrected misconception is a history entry on its topic and not a kind
    of its own, see "Knowledge model" in docs/knowledge-model.md: a record in
    reported history, its `details` `{"misconception": true, "believed": ...,
    "correct": ...}`, its content built from the two under the topic's name,
    and its when `occurred_at`, or when it was entered.

    The topic is kept as it is named, an alias included, and read through
    it, as any entry's. Refused for a topic the user does not have."""
    topic = await _canonical_topic(conn, slug)
    return await _write(
        conn,
        NewItem(
            kind="history",
            content=(
                f"Misconception about {topic.name}\n"
                f"Believed: {believed}\nCorrect: {correct}"
            ),
            category=None,
            topics=(slug,),
            entities=(),
            occurred_at=occurred_at,
            occurred_precision=occurred_precision,
            details={"misconception": True, "believed": believed, "correct": correct},
        ),
        now=now,
    )
