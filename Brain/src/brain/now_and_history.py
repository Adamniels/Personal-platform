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

from psycopg import AsyncConnection
from psycopg.rows import TupleRow

from brain import items

type Connection = AsyncConnection[TupleRow]

# How long a state goes without support before it is flagged as worth re
# checking. One crude threshold, as the trust model says: no decay curve and
# no half life until the evaluation harness can earn one.
STALE_AFTER = timedelta(days=60)


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
        f"SELECT {items.COLUMNS}, support.last_supported_at,"
        " (nature = 'claim' AND kind = 'state' AND status = 'active'"
        "  AND support.last_supported_at < %(now)s - %(stale_after)s)"
        " FROM items i CROSS JOIN LATERAL ("
        "  SELECT max(e.occurred_at) AS last_supported_at"
        "  FROM item_evidence ev JOIN events e ON e.id = ev.event_id"
        "  WHERE ev.item_id = i.id AND ev.kind = 'supports') support"
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
