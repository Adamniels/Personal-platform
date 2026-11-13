"""The knowledge view: every topic the owner has, with how well he knows it,
what he is doing about it, and how much history it carries. See "How level is
represented" in docs/knowledge-model.md.

It is a read over `topics` and `items` and owns no rows: a level and a focus
are items, set with `brain.items.set_on_topic`, and a misconception is a
history item. Every function takes a connection from `brain.db.transaction`,
and reads for that transaction's user.
"""

from dataclasses import dataclass
from datetime import datetime
from uuid import UUID

from psycopg import AsyncConnection
from psycopg.rows import TupleRow

from brain import topics

type Connection = AsyncConnection[TupleRow]


@dataclass(frozen=True)
class Setting:
    """A topic's active level or focus: the entry, its value, and when it
    was last set, which is when its newest version was made."""

    id: UUID
    value: str
    set_at: datetime


@dataclass(frozen=True)
class Row:
    id: UUID
    slug: str
    name: str
    state: topics.State
    level: Setting | None
    focus: Setting | None
    # The active history entries on it, its aliases' included.
    history_count: int


async def view(
    conn: Connection,
    *,
    level: str | None,
    focus: str | None,
    state: str | None,
) -> list[Row]:
    """One row per topic of the current user's that is not an alias, by
    slug, each alias folded into its target: its active level and focus, or
    None for each it has not, and its count of active history entries.
    Narrowed by each filter that is given, the level's value, the focus's and
    the topic's state; a filter on a value leaves out the topics without
    one. Should two topics that each had a level be marked the same, the
    newest active one is the level, as `set_on_topic` reads it."""
    cursor = await conn.execute(
        """
        WITH subject AS (
            SELECT s.item_id, coalesce(t.alias_of, t.id) AS topic_id
            FROM item_subjects s JOIN topics t ON t.id = s.topic_id
        ), newest AS (
            SELECT DISTINCT ON (subject.topic_id, i.kind)
                   subject.topic_id, i.kind, i.id, i.value, i.created_at
            FROM items i JOIN subject ON subject.item_id = i.id
            WHERE i.status = 'active' AND i.kind IN ('topic_level', 'topic_focus')
            ORDER BY subject.topic_id, i.kind, i.created_at DESC, i.id DESC
        ), history AS (
            SELECT subject.topic_id, count(DISTINCT i.id) AS entries
            FROM items i JOIN subject ON subject.item_id = i.id
            WHERE i.status = 'active' AND i.kind = 'history'
            GROUP BY subject.topic_id
        )
        SELECT t.id, t.slug, t.name, t.state,
               l.id, l.value, l.created_at, f.id, f.value, f.created_at,
               coalesce(h.entries, 0)
        FROM topics t
        LEFT JOIN newest l ON l.topic_id = t.id AND l.kind = 'topic_level'
        LEFT JOIN newest f ON f.topic_id = t.id AND f.kind = 'topic_focus'
        LEFT JOIN history h ON h.topic_id = t.id
        WHERE t.alias_of IS NULL
          AND (%(level)s::text IS NULL OR l.value = %(level)s)
          AND (%(focus)s::text IS NULL OR f.value = %(focus)s)
          AND (%(state)s::text IS NULL OR t.state = %(state)s)
        ORDER BY t.slug
        """,
        {"level": level, "focus": focus, "state": state},
    )
    return [
        Row(
            id=topic_id,
            slug=slug,
            name=name,
            state=topic_state,
            level=Setting(level_id, level_value, level_at)
            if level_id is not None
            else None,
            focus=Setting(focus_id, focus_value, focus_at)
            if focus_id is not None
            else None,
            history_count=entries,
        )
        for (
            topic_id,
            slug,
            name,
            topic_state,
            level_id,
            level_value,
            level_at,
            focus_id,
            focus_value,
            focus_at,
            entries,
        ) in await cursor.fetchall()
    ]
