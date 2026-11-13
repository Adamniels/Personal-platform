"""What is going on now, and what has happened: two reads over `items` that
own no rows. A state is a claim about now, so it is read with when it was
last supported and whether that is long enough ago to re check; a history
entry or a decision is read by when it happened. See "Claim versus record" in
docs/knowledge-model.md and "Confidence, currency and status are three
different things" in docs/trust-model.md.

Every function takes a connection from `brain.db.transaction`, and reads for
that transaction's user. Time is passed in, never read from the clock here.
"""

from dataclasses import dataclass
from datetime import datetime, timedelta
from uuid import UUID

from psycopg import AsyncConnection
from psycopg.rows import TupleRow

from brain import items

type Connection = AsyncConnection[TupleRow]

# How long a state goes without support before it is flagged as worth re
# checking. One crude threshold, as the trust model says: no decay curve and
# no half life until the evaluation harness can earn one.
STALE_AFTER = timedelta(days=60)


# When each item of `items i` was last supported, the newest `occurred_at`
# among the events supporting it, as `support.last_supported_at`.
_SUPPORT = (
    " CROSS JOIN LATERAL ("
    "  SELECT max(e.occurred_at) AS last_supported_at"
    "  FROM item_evidence ev JOIN events e ON e.id = ev.event_id"
    "  WHERE ev.item_id = i.id AND ev.kind = 'supports') support"
)
# Whether it is stale at %(now)s: only an active claim of kind state, last
# supported more than %(stale_after)s before.
_STALE = (
    "(i.nature = 'claim' AND i.kind = 'state' AND i.status = 'active'"
    " AND support.last_supported_at < %(now)s - %(stale_after)s)"
)


@dataclass(frozen=True)
class Currency:
    """When an item was last supported, and whether that makes it stale, as
    the now view reads it. Computed when read and never stored."""

    last_supported_at: datetime
    stale: bool


async def currency(
    conn: Connection, item_ids: list[UUID], *, now: datetime
) -> dict[UUID, Currency]:
    """The currency of each of the current user's items in `item_ids` as it
    reads at `now`, by the now view's rule, so any list of items can show
    it: every item, active or not, has a last support, since every item is
    made with the event that supports it, and only an active state can be
    stale. An id the user does not have is left out."""
    cursor = await conn.execute(
        f"SELECT i.id, support.last_supported_at, {_STALE}"
        f" FROM items i{_SUPPORT} WHERE i.id = ANY(%(ids)s)",
        {"ids": item_ids, "now": now, "stale_after": STALE_AFTER},
    )
    found = {}
    for item_id, last_supported_at, stale in await cursor.fetchall():
        assert last_supported_at is not None, item_id
        found[item_id] = Currency(last_supported_at, bool(stale))
    return found


@dataclass(frozen=True)
class NowEntry:
    item: items.Item
    # The newest `occurred_at` among the events supporting it, computed in
    # the query and never stored.
    last_supported_at: datetime
    # True when it was last supported more than STALE_AFTER before the time
    # the read was made at.
    stale: bool


async def now(conn: Connection, *, now: datetime) -> list[NowEntry]:
    """The current user's active state entries as they read at `now`,
    newest support first, each with when it was last supported and whether
    that is more than `STALE_AFTER` before `now`. Only a claim of kind
    `state` is ever stale: a record has no currency, so it has no flag."""
    cursor = await conn.execute(
        f"SELECT {items.COLUMNS}, support.last_supported_at, {_STALE}"
        f" FROM items i{_SUPPORT}"
        " WHERE i.kind = 'state' AND i.status = 'active'"
        " ORDER BY support.last_supported_at DESC, i.id DESC",
        {"now": now, "stale_after": STALE_AFTER},
    )
    rows = await cursor.fetchall()
    found = await items.from_rows(conn, rows)
    entries = []
    for item, row in zip(found, rows, strict=True):
        last_supported_at, stale = row[len(items.FIELDS) :]
        # Every item is made with the event that supports it.
        assert last_supported_at is not None
        entries.append(NowEntry(item, last_supported_at, bool(stale)))
    return entries


@dataclass(frozen=True)
class HistoryEntry:
    item: items.Item
    # For a decision another one replaced, the one that did; else None.
    replaced_by: UUID | None


async def history(
    conn: Connection, *, topic: str | None, entity: str | None
) -> list[HistoryEntry]:
    """The current user's history entries and decisions, by when each
    happened, oldest first, so a course from years ago entered today comes
    before a decision from last week. Active ones, and the decisions another
    decision replaced, each with the one that replaced it: what was decided
    stays, though it no longer holds. A retired entry, and an older version
    of an edited one, are left out. Narrowed by topic and named thing as
    `items.listing` narrows."""
    cursor = await conn.execute(
        f"SELECT {items.COLUMNS}, replaced_by FROM ("
        "  SELECT i.*, ("
        "   SELECT r.from_item FROM item_relations r"
        "   JOIN items newer ON newer.id = r.from_item"
        "   WHERE r.to_item = i.id AND r.kind = 'supersedes' AND r.ended_at IS NULL"
        "   AND newer.lineage_id <> i.lineage_id"
        "   ORDER BY r.created_at DESC, r.id DESC LIMIT 1) AS replaced_by"
        "  FROM items i"
        "  WHERE i.kind IN ('history', 'decision')"
        f"  AND {items.SUBJECT_FILTERS}) i"
        " WHERE i.status = 'active'"
        " OR (i.kind = 'decision' AND i.inactive_reason = 'superseded'"
        "  AND i.replaced_by IS NOT NULL)"
        " ORDER BY i.occurred_at, i.created_at, i.id",
        {"topic": topic, "entity": entity},
    )
    rows = await cursor.fetchall()
    found = await items.from_rows(conn, rows)
    return [
        HistoryEntry(item, row[len(items.FIELDS)])
        for item, row in zip(found, rows, strict=True)
    ]
