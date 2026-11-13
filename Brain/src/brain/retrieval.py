"""The building blocks every answer the brain gives is made of: who the
person is, what they know about some topics, the history of one subject, and
what is going on now. See "Retrieval: per feature calls over shared
primitives" in docs/brain-architecture.md.

The `ask` endpoints, the evaluation cases and every later per feature call
read through these four and nothing else, so a feature never merges anything
itself. Each returns items in one shape, `AnswerItem`, which carries the
reasons the item is believed: the events behind it, how many support it and
how many contradict it, counted per verb kind, and when it was last
supported. There is no relevance score, no confidence number and no weight:
those are the facts such a score would be computed from, and the score waits
until the evaluation cases can test one, see "Confidence, currency and
status are three different things" in docs/trust-model.md.

Every block takes a connection from `brain.db.transaction`, so it reads for
that transaction's user and no other, and the time, passed in and never read
from the clock. It never calls a model and never leaves the database.

A question is asked about now or about history, its `mode`. About now, only
active items, newest support first, with a state flagged stale once it has
gone unsupported long enough. About history, age is ignored: nothing is
stale, the order is when each thing happened, oldest first, and the history
of a subject keeps the decisions another decision replaced. A retired item is
never in an answer, nor an older version of an edited one.
"""

from collections.abc import Sequence
from dataclasses import dataclass
from datetime import datetime
from typing import Any, Literal, get_args
from uuid import UUID

from psycopg import AsyncConnection
from psycopg.rows import TupleRow

from brain import events, items, now_and_history

type Connection = AsyncConnection[TupleRow]
type Mode = Literal["now", "history"]

# The kinds of verb, as the verbs table checks them: what an event's support
# is counted by.
VerbKind = Literal[
    "exposure", "engagement", "judgment", "assertion", "decision", "activity"
]
VERB_KINDS: tuple[str, ...] = get_args(VerbKind)

# The kinds of claim that go stale: a state, as the now view reads it, and
# only while active. A record has no currency at all, and the other claims
# are not read for it until the evaluation cases can say they should be.
STALE_KINDS = frozenset({"state"})


@dataclass(frozen=True)
class Reason:
    """One event behind an item: what was done, whether it is for the item
    or against it, what it says, where it came from and when."""

    verb: str
    kind: Literal["supports", "contradicts"]
    text: str
    origin: str
    occurred_at: datetime
    occurred_precision: events.Precision
    recorded_at: datetime


@dataclass(frozen=True)
class Support:
    """How many events support an item and how many contradict it, each
    counted per verb kind, every kind present, a zero included."""

    supports: dict[str, int]
    contradicts: dict[str, int]


@dataclass(frozen=True)
class AnswerItem:
    """An item as every answer gives it: the item, its subjects among it,
    and its reasons. Nothing here is stored: each is computed from the
    evidence when the question is asked."""

    item: items.Item
    # The newest `occurred_at` among the events supporting it.
    last_supported_at: datetime
    # Asked about now, a state last supported more than STALE_AFTER before
    # the time asked at; always False asked about history.
    stale: bool
    support: Support
    # Oldest recorded first, as a single read gives them.
    evidence: tuple[Reason, ...]


def _stale(item: items.Item, last_supported_at: datetime, *, now: datetime) -> bool:
    """The now view's rule, `brain.now_and_history.now`, over one item."""
    return (
        item.nature == "claim"
        and item.kind in STALE_KINDS
        and item.status == "active"
        and last_supported_at < now - now_and_history.STALE_AFTER
    )


# A decision another decision replaced, rather than an older version of the
# same entry: what the history of a subject keeps, asked about history.
_REPLACED = (
    "i.kind = 'decision' AND i.inactive_reason = 'superseded' AND EXISTS ("
    " SELECT FROM item_relations r JOIN items newer ON newer.id = r.from_item"
    " WHERE r.to_item = i.id AND r.kind = 'supersedes' AND r.ended_at IS NULL"
    " AND newer.lineage_id <> i.lineage_id)"
)


async def _answered(
    conn: Connection,
    condition: str,
    params: dict[str, Any],
    *,
    now: datetime,
    mode: Mode,
    replaced: bool = False,
) -> list[AnswerItem]:
    """The current user's items that meet `condition`, a WHERE clause over
    `items i` with `params`, as `mode` reads them: active ones, and with
    `replaced`, the decisions another one replaced; ordered as the module
    says. In history order, an item that does not say when it happened is
    placed by when the event that made it says it did."""
    standing = "i.status = 'active'"
    if replaced:
        standing = f"({standing} OR ({_REPLACED}))"
    cursor = await conn.execute(
        f"SELECT {items.COLUMNS}, coalesce(i.occurred_at, ("
        "  SELECT made.occurred_at FROM events made"
        "  WHERE made.id = i.created_by_event))"
        f" FROM items i WHERE {standing} AND ({condition})",
        params,
    )
    rows = await cursor.fetchall()
    found = await items.from_rows(conn, rows)
    happened = {
        item.id: row[len(items.FIELDS)] for item, row in zip(found, rows, strict=True)
    }
    reasons = await _reasons(conn, [item.id for item in found])
    answered = []
    for item in found:
        evidence = reasons[item.id]
        supporting = [r.occurred_at for r, _ in evidence if r.kind == "supports"]
        # Every item is made with the event that supports it.
        assert supporting, item.id
        last_supported_at = max(supporting)
        support = Support(
            supports=_counted(evidence, "supports"),
            contradicts=_counted(evidence, "contradicts"),
        )
        answered.append(
            AnswerItem(
                item=item,
                last_supported_at=last_supported_at,
                stale=mode == "now" and _stale(item, last_supported_at, now=now),
                support=support,
                evidence=tuple(reason for reason, _ in evidence),
            )
        )
    if mode == "now":
        answered.sort(key=lambda a: (a.last_supported_at, a.item.id), reverse=True)
    else:
        answered.sort(key=lambda a: (happened[a.item.id], a.item.created_at, a.item.id))
    return answered


def _counted(evidence: list[tuple[Reason, str]], kind: str) -> dict[str, int]:
    counted = dict.fromkeys(VERB_KINDS, 0)
    for reason, verb_kind in evidence:
        if reason.kind == kind:
            counted[verb_kind] += 1
    return counted


async def _reasons(
    conn: Connection, item_ids: list[UUID]
) -> dict[UUID, list[tuple[Reason, str]]]:
    """Each item's evidence, oldest recorded first, each with its verb's
    kind."""
    found: dict[UUID, list[tuple[Reason, str]]] = {i: [] for i in item_ids}
    cursor = await conn.execute(
        "SELECT ev.item_id, e.verb, ev.kind, e.evidence, e.origin, e.occurred_at,"
        " e.occurred_precision, e.recorded_at, v.kind"
        " FROM item_evidence ev JOIN events e ON e.id = ev.event_id"
        " JOIN verbs v ON v.verb = e.verb"
        " WHERE ev.item_id = ANY(%s) ORDER BY e.recorded_at, ev.id",
        (item_ids,),
    )
    for item_id, *reason, verb_kind in await cursor.fetchall():
        found[item_id].append((Reason(*reason), verb_kind))
    return found


@dataclass(frozen=True)
class Profile:
    """Who the person is: their profile entries, grouped by category, every
    category in the order `brain.items.CATEGORIES` lists them, an empty one
    included; then their rules."""

    categories: tuple[tuple[str, tuple[AnswerItem, ...]], ...]
    rules: tuple[AnswerItem, ...]


async def profile(conn: Connection, *, now: datetime, mode: Mode) -> Profile:
    """The current user's profile entries and rules, as `mode` reads them. A
    topic's focus lives in the profile store too, but is what `knowledge`
    answers for its topic."""
    found = await _answered(
        conn, "i.kind IN ('profile', 'rule')", {}, now=now, mode=mode
    )
    return Profile(
        categories=tuple(
            (
                category,
                tuple(
                    a
                    for a in found
                    if a.item.kind == "profile" and a.item.category == category
                ),
            )
            for category in items.CATEGORIES
        ),
        rules=tuple(a for a in found if a.item.kind == "rule"),
    )


@dataclass(frozen=True)
class TopicKnowledge:
    """What the person knows about one topic, its aliases folded in: its
    level and its focus, or None for each it has not, its corrected
    misconceptions, and its other history."""

    # The topic it is about, the target of an alias.
    topic: events.TopicRef
    level: AnswerItem | None
    focus: AnswerItem | None
    misconceptions: tuple[AnswerItem, ...]
    history: tuple[AnswerItem, ...]


def _misconception(item: items.Item) -> bool:
    return item.kind == "history" and item.details.get("misconception") is True


def _setting(found: list[AnswerItem], kind: str) -> AnswerItem | None:
    """The newest active entry of `kind`, a level or a focus: should two
    topics that each had one be marked the same, the newest stands, as
    `brain.items.set_on_topic` reads it."""
    settings = [a for a in found if a.item.kind == kind]
    if not settings:
        return None
    return max(settings, key=lambda a: (a.item.created_at, a.item.id))


async def knowledge(
    conn: Connection, topic_ids: Sequence[UUID], *, now: datetime, mode: Mode
) -> dict[UUID, TopicKnowledge]:
    """What the current user knows about each of `topic_ids`, keyed by the
    id given, each read as the topic it resolves to, as `mode` reads it. An
    item about an alias is about its target. An id the user has no topic by
    is left out. Only the topic itself, not the topics under it."""
    cursor = await conn.execute(
        "SELECT given.id, t.id, t.slug, t.name FROM topics given"
        " JOIN topics t ON t.id = coalesce(given.alias_of, given.id)"
        " WHERE given.id = ANY(%s)",
        (list(topic_ids),),
    )
    resolved = {
        given: events.TopicRef(topic_id, slug, name)
        for given, topic_id, slug, name in await cursor.fetchall()
    }
    canonical = {topic.id: topic for topic in resolved.values()}
    found = await _answered(
        conn,
        "i.kind IN ('topic_level', 'topic_focus', 'history') AND EXISTS ("
        " SELECT FROM item_subjects s JOIN topics named ON named.id = s.topic_id"
        " WHERE s.item_id = i.id"
        " AND coalesce(named.alias_of, named.id) = ANY(%(topics)s))",
        {"topics": list(canonical)},
        now=now,
        mode=mode,
    )
    answers = {}
    for topic_id, topic in canonical.items():
        about = [a for a in found if topic_id in {t.id for t in a.item.topics}]
        answers[topic_id] = TopicKnowledge(
            topic=topic,
            level=_setting(about, "topic_level"),
            focus=_setting(about, "topic_focus"),
            misconceptions=tuple(a for a in about if _misconception(a.item)),
            history=tuple(
                a
                for a in about
                if a.item.kind == "history" and not _misconception(a.item)
            ),
        )
    return {given: answers[topic.id] for given, topic in resolved.items()}


@dataclass(frozen=True)
class Subject:
    """What a history is asked about: a topic or a named thing, by id."""

    kind: Literal["topic", "entity"]
    id: UUID


async def history(
    conn: Connection, subject: Subject, *, now: datetime, mode: Mode
) -> list[AnswerItem]:
    """The current user's records and decisions about `subject`, as `mode`
    reads them; asked about history, with each decision another one
    replaced, inactive and superseded, beside the one that replaced it. A
    topic is read through aliases on either side, and only itself."""
    if subject.kind == "topic":
        about = (
            "EXISTS (SELECT FROM item_subjects s"
            " JOIN topics named ON named.id = s.topic_id"
            " JOIN topics wanted ON wanted.id = %(subject)s"
            " WHERE s.item_id = i.id"
            " AND coalesce(named.alias_of, named.id)"
            "  = coalesce(wanted.alias_of, wanted.id))"
        )
    else:
        about = (
            "EXISTS (SELECT FROM item_subjects s"
            " WHERE s.item_id = i.id AND s.entity_id = %(subject)s)"
        )
    return await _answered(
        conn,
        f"i.kind IN ('history', 'decision') AND {about}",
        {"subject": subject.id},
        now=now,
        mode=mode,
        replaced=mode == "history",
    )


async def now(conn: Connection, *, now: datetime, mode: Mode) -> list[AnswerItem]:
    """What is going on now for the current user: their states, as `mode`
    reads them."""
    return await _answered(conn, "i.kind = 'state'", {}, now=now, mode=mode)
