"""The building blocks every answer the brain gives is made of: who the
person is, what they know about some topics, the history of one subject,
what is going on now, and what holds some words. See "Retrieval: per feature
calls over shared primitives" in docs/brain-architecture.md.

The `ask` endpoints, search, the evaluation cases and every later per
feature call read through these five and nothing else, so a feature never
merges anything itself. Each returns items in one shape, `AnswerItem`,
which carries the reasons the item is believed: the events behind it, how
many support it and how many contradict it, counted per verb kind, and when
it was last supported. There is no relevance score, no confidence number and no weight:
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

Beside the five: `narrower` and `related`, the topics around one, so an
answer about a broad topic brings the topics under it; and `conflicts` and
`ranked`, two items of one answer the owner marked as contradicting, both
kept, ordered by their scopes and never decided between, see
"Conflicting decisions before consolidation" in docs/event-model.md.
"""

from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from datetime import datetime
from typing import Any, Literal, get_args
from uuid import UUID

from psycopg import AsyncConnection
from psycopg.rows import TupleRow

from brain import events, items, now_and_history
from brain.scope import Scope, compare

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
    inactive: bool = False,
) -> list[AnswerItem]:
    """The current user's items that meet `condition`, a WHERE clause over
    `items i` with `params`, as `mode` reads them: active ones, with
    `replaced` the decisions another one replaced too, and with `inactive`
    every one, retired and superseded alike; ordered as the module says. In
    history order, an item that does not say when it happened is placed by
    when the event that made it says it did."""
    standing = "i.status = 'active'"
    if inactive:
        standing = "TRUE"
    elif replaced:
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

    categories: tuple[tuple[items.Category, tuple[AnswerItem, ...]], ...]
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
class Narrower:
    """A topic under the one asked about, directly or through others."""

    topic: events.TopicRef
    # The slugs that led to it: the topic asked about first, then each topic
    # on the way, down to the one it is directly under.
    via: tuple[str, ...]


async def _holding(
    conn: Connection, kind: str
) -> list[tuple[events.TopicRef, events.TopicRef]]:
    """The current user's relations of `kind` that hold, each end read
    through its alias, from_topic first."""
    cursor = await conn.execute(
        "SELECT a.id, a.slug, a.name, b.id, b.slug, b.name FROM topic_relations r"
        " JOIN topics named_a ON named_a.id = r.from_topic"
        " JOIN topics a ON a.id = coalesce(named_a.alias_of, named_a.id)"
        " JOIN topics named_b ON named_b.id = r.to_topic"
        " JOIN topics b ON b.id = coalesce(named_b.alias_of, named_b.id)"
        " WHERE r.kind = %s AND r.ended_at IS NULL",
        (kind,),
    )
    return [
        (events.TopicRef(*row[:3]), events.TopicRef(*row[3:]))
        for row in await cursor.fetchall()
    ]


async def _canonical(conn: Connection, topic_id: UUID) -> events.TopicRef | None:
    cursor = await conn.execute(
        "SELECT t.id, t.slug, t.name FROM topics given"
        " JOIN topics t ON t.id = coalesce(given.alias_of, given.id)"
        " WHERE given.id = %s",
        (topic_id,),
    )
    row = await cursor.fetchone()
    return events.TopicRef(*row) if row is not None else None


async def narrower(conn: Connection, topic_id: UUID) -> list[Narrower]:
    """Every topic under `topic_id`, to any depth, by the broader_than
    relations that hold, read through aliases, nearest first and then by
    slug. Each is listed once, with the shortest chain that leads to it, the
    first by slug among equally short ones. The walk visits a topic once, so
    it ends even on a loop the API would have refused, and the topic asked
    about is never under itself. Nothing for a topic the user does not
    have."""
    root = await _canonical(conn, topic_id)
    if root is None:
        return []
    under: dict[UUID, list[events.TopicRef]] = {}
    for broader, below in await _holding(conn, "broader_than"):
        under.setdefault(broader.id, []).append(below)
    found: dict[UUID, Narrower] = {}
    reached: dict[UUID, tuple[str, ...]] = {root.id: (root.slug,)}
    frontier = [root]
    # Breadth first, so the first chain to reach a topic is a shortest one.
    while frontier:
        following = []
        for topic in frontier:
            for below in sorted(under.get(topic.id, []), key=lambda t: t.slug):
                if below.id in reached:
                    continue
                via = reached[topic.id]
                found[below.id] = Narrower(topic=below, via=via)
                reached[below.id] = (*via, below.slug)
                following.append(below)
        frontier = following
    return sorted(found.values(), key=lambda n: (len(n.via), n.topic.slug))


async def related(conn: Connection, topic_id: UUID) -> list[events.TopicRef]:
    """The topics related to `topic_id` by related_to relations that hold,
    made from either end, read through aliases, each once, by slug. Only
    named: what is known about a related topic is not part of the answer
    about this one. Nothing for a topic the user does not have."""
    root = await _canonical(conn, topic_id)
    if root is None:
        return []
    found: dict[UUID, events.TopicRef] = {}
    for one, other in await _holding(conn, "related_to"):
        if one.id == root.id and other.id != root.id:
            found[other.id] = other
        elif other.id == root.id and one.id != root.id:
            found[one.id] = one
    return sorted(found.values(), key=lambda t: t.slug)


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


# Why one of a conflicting pair is preferred, or that neither can be.
type ConflictReason = Literal[
    "narrower scope", "newer at equal scope", "incomparable scopes"
]


@dataclass(frozen=True)
class Conflict:
    """Two items in one answer the owner marked as contradicting. Both stay
    in the answer: `prefer` only orders them, and is None when their scopes
    are incomparable, which no ordering settles."""

    # The relation's two ends, the one it was marked from first.
    items: tuple[UUID, UUID]
    prefer: UUID | None
    reason: ConflictReason


def _scope(item: items.Item) -> Scope:
    # An item's topics are read through their aliases already.
    return Scope.of([t.id for t in item.topics], [e.id for e in item.entities])


def _judged(one: items.Item, other: items.Item) -> Conflict:
    """The conflict between `one` and `other`, as "Conflicting decisions
    before consolidation" in docs/event-model.md has it: a narrower scope is
    preferred whatever its age, at an equal scope the newer, and between
    incomparable scopes neither."""
    pair = (one.id, other.id)
    match compare(_scope(one), _scope(other)):
        case "narrower":
            return Conflict(pair, one.id, "narrower scope")
        case "broader":
            return Conflict(pair, other.id, "narrower scope")
        case "equal":
            newer = max(one, other, key=lambda i: (i.created_at, i.id))
            return Conflict(pair, newer.id, "newer at equal scope")
        case "incomparable":
            return Conflict(pair, None, "incomparable scopes")


async def conflicts(conn: Connection, answered: Iterable[AnswerItem]) -> list[Conflict]:
    """The conflicts among `answered`, the items of one answer: every pair
    of them with a `contradicts` relation that holds, both active, oldest
    marked first. Today the owner marks every one by hand; nothing detects a
    contradiction on its own until consolidation exists. A superseded or
    retired item is never part of one, even where an answer about history
    holds it, since it no longer claims anything."""
    standing = {a.item.id: a.item for a in answered if a.item.status == "active"}
    if len(standing) < 2:
        return []
    cursor = await conn.execute(
        "SELECT from_item, to_item FROM item_relations"
        " WHERE kind = 'contradicts' AND ended_at IS NULL"
        " AND from_item = ANY(%(ids)s) AND to_item = ANY(%(ids)s)"
        " ORDER BY created_at, id",
        {"ids": list(standing)},
    )
    return [
        _judged(standing[one], standing[other])
        for one, other in await cursor.fetchall()
    ]


def ranked(
    answered: Sequence[AnswerItem], found: Iterable[Conflict]
) -> list[AnswerItem]:
    """`answered` with each preferred item moved to just before the first
    item it is preferred over, and otherwise in the order it had: what an
    answer about now does with its conflicts. Both items stay.

    Preference never loops: a narrower scope is a strict superset, and at an
    equal scope the newer wins, so following it only ever leads to narrower
    or newer items."""
    present = {a.item.id for a in answered}
    over: dict[UUID, list[UUID]] = {}
    for conflict in found:
        if conflict.prefer is None:
            continue
        beaten = next(i for i in conflict.items if i != conflict.prefer)
        if conflict.prefer in present and beaten in present:
            over.setdefault(beaten, []).append(conflict.prefer)
    if not over:
        return list(answered)
    by_id = {a.item.id: a for a in answered}
    position = {a.item.id: n for n, a in enumerate(answered)}
    placed: set[UUID] = set()
    order: list[AnswerItem] = []

    def place(item_id: UUID) -> None:
        if item_id in placed:
            return
        placed.add(item_id)
        for winner in sorted(over.get(item_id, []), key=position.__getitem__):
            place(winner)
        order.append(by_id[item_id])

    for each in answered:
        place(each.item.id)
    return order


# Searching memory by its words, with Postgres' own text search over the
# `search` columns migration 0005 adds, in the configuration they are built
# in: stemmed, so `indexing` finds `indexes`, and never by meaning.

# The text search configuration of both `search` columns.
SEARCH_CONFIG = "english"


def plain_words(query: str) -> str:
    """`query` as plain words, for `websearch_to_tsquery` to make a search of
    in which every word is required and nothing is an operator: every
    character that is not a letter or a digit a space, `&`, `|`, `!`, `:`,
    `*`, `-`, quotes and parentheses among them, and the word `or`, in any
    letter case, left out, since it would read it as one. `or` is a stop word
    of the configuration, so leaving it out finds nothing less. Empty when no
    word is left."""
    spaced = "".join(c if c.isalnum() else " " for c in query)
    return " ".join(word for word in spaced.split() if word.lower() != "or")


def _escaped(sql: str) -> str:
    """The SQL text `sql` evaluates to, escaped for a page as `html.escape`
    escapes it, `&` first."""
    # Each character as a SQL literal, and the literal it becomes.
    for raw, escaped in (
        ("'&'", "'&amp;'"),
        ("'<'", "'&lt;'"),
        ("'>'", "'&gt;'"),
        ("'\"'", "'&quot;'"),
        ("''''", "'&#x27;'"),
    ):
        sql = f"replace({sql}, {raw}, {escaped})"
    return sql


# An item's own words as one text, what its `search` column is built from:
# content, its level or focus, its category and every text value in details,
# a line each.
_ITEM_TEXT = (
    "concat_ws(E'\\n', i.content, i.value, i.category, ("
    " SELECT string_agg(s #>> '{}', E'\\n') FROM jsonb_path_query("
    "  i.details, 'strict $.** ? (@.type() == \"string\")') s))"
)

# How an excerpt is cut: about 15 to 35 words around the matched ones, each
# matched word wrapped in <mark>. ts_headline copies the text it is given,
# minus anything it reads as a tag, so it is given the text escaped first,
# and the marks are the only markup in what comes back.
_HEADLINE = "StartSel=<mark>, StopSel=</mark>, MinWords=15, MaxWords=35"


@dataclass(frozen=True)
class Found:
    """An item a search found, as every answer gives it, and an excerpt of
    where its words are: its own text where that matches, otherwise the
    evidence of the newest recorded event behind it that does. The excerpt is
    HTML: the matched words in <mark>, every other character escaped."""

    answered: AnswerItem
    excerpt: str


async def search(
    conn: Connection,
    query: str,
    *,
    now: datetime,
    kinds: Sequence[items.Kind] = (),
    inactive: bool = False,
    limit: int,
) -> list[Found]:
    """The current user's items holding every word of `query`, read as
    `plain_words`, in what they say or in the evidence of an event linked to
    them for or against: of `kinds`, any when empty; active ones, or every
    one with `inactive`; newest `last_supported_at` first, at most `limit`.
    There is no rank: the order is when each was last supported, and Postgres'
    own rank is neither read nor returned, since it would be a relevance
    score by another name. A query with no word left finds nothing."""
    words = plain_words(query)
    if not words:
        return []
    params: dict[str, Any] = {
        "words": words,
        "kinds": list(kinds),
        "inactive": inactive,
        "limit": limit,
    }
    cursor = await conn.execute(
        "WITH asked AS ("
        f" SELECT websearch_to_tsquery('{SEARCH_CONFIG}', %(words)s) AS q),"
        " matched AS ("
        "  SELECT i.id FROM items i, asked WHERE i.search @@ asked.q"
        "  UNION"
        "  SELECT ev.item_id FROM events e"
        "  JOIN item_evidence ev ON ev.event_id = e.id, asked"
        "  WHERE e.search @@ asked.q),"
        " kept AS ("
        "  SELECT i.id, (SELECT max(e.occurred_at) FROM item_evidence ev"
        "   JOIN events e ON e.id = ev.event_id"
        "   WHERE ev.item_id = i.id AND ev.kind = 'supports') AS last_supported_at"
        "  FROM items i WHERE i.id IN (SELECT id FROM matched)"
        "  AND (%(inactive)s OR i.status = 'active')"
        "  AND (cardinality(%(kinds)s::text[]) = 0 OR i.kind = ANY(%(kinds)s::text[]))"
        "  ORDER BY last_supported_at DESC, i.id DESC LIMIT %(limit)s)"
        " SELECT i.id, CASE WHEN i.search @@ asked.q THEN"
        f"  ts_headline('{SEARCH_CONFIG}', {_escaped(_ITEM_TEXT)}, asked.q,"
        f"   '{_HEADLINE}')"
        " ELSE ("
        f"  SELECT ts_headline('{SEARCH_CONFIG}', {_escaped('e.evidence')},"
        f"   asked.q, '{_HEADLINE}')"
        "  FROM item_evidence ev JOIN events e ON e.id = ev.event_id"
        "  WHERE ev.item_id = i.id AND e.search @@ asked.q"
        "  ORDER BY e.recorded_at DESC, ev.id DESC LIMIT 1) END"
        " FROM kept JOIN items i ON i.id = kept.id, asked",
        params,
    )
    excerpts: dict[UUID, str] = dict(await cursor.fetchall())
    found = await _answered(
        conn,
        "i.id = ANY(%(ids)s)",
        {"ids": list(excerpts)},
        now=now,
        mode="now",
        inactive=inactive,
    )
    # The now order, newest support first, is the order the query kept.
    return [Found(answered, excerpts[answered.item.id]) for answered in found]
