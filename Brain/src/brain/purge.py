"""`brain purge <account-id> <item-id>`: removes one entry from one account's
memory for good, from a terminal, as the owning role. See "Purging" in
Brain/README.md, and "Nothing is destroyed, and only the owner retires" in
docs/trust-model.md for why it is the one way anything is destroyed.

What goes: every version of the entry, the items sharing the given item's
`lineage_id`; their `item_subjects`, their `item_relations` on either side
and their `item_evidence`; and every event linked to any of those rows, by
`subject_item`, by `item_evidence` or as the event that made an item or a
relation, that nothing left behind still references. An event something
else still rests on stays, and one that was about a removed item no longer
names it. Topics and named things never go.

The owning role may delete, which brain_app may not, and that is the whole
of the bypass. Row level security is forced on every table, so it holds for
the owner too: the command sets `brain.user_id` to the account for its one
transaction, as the door does, and sees and removes that account's rows
alone. It writes no event about the purge, since nothing about the entry is
to remain; what it prints is the only record.
"""

import os
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from typing import TextIO
from uuid import UUID

import psycopg
from psycopg import sql

from brain import db

type Connection = psycopg.Connection[tuple[object, ...]]

# The one answer that proceeds, as a whole line.
YES = "yes"

# How much of a row's own text the listing shows, on one line.
TEXT_MAX = 80

# events.subject_item names an item, and items.created_by_event an event, so
# neither table's rows can go first. This reference, made deferrable for the
# import by migrations/0006_deferrable_event_item.sql, waits for the end of
# the purge's statements instead, and is checked before the transaction ends.
DEFERRED = "events_user_id_subject_item_fkey"

# The tables in the order the listing names them and the purge removes them:
# what hangs on an item before the items, and the events last.
TABLES = ("item_evidence", "item_subjects", "item_relations", "items", "events")


class NotFound(Exception):
    """The account has no such item, or it is another account's: the two are
    the same to the command, since row level security hides the second."""


@dataclass(frozen=True)
class Row:
    """One row the listing names: its table, its id, and its text."""

    table: str
    id: str
    text: str


@dataclass(frozen=True)
class Plan:
    """What a purge removes, read under lock in its transaction, and the
    events it keeps that were linked to what it removes."""

    items: list[UUID]
    removed: list[Row]
    # Events still referenced by something that stays, each with whether its
    # subject_item named a removed item, which the purge clears.
    kept: list[tuple[Row, bool]]

    def ids(self, table: str) -> list[str]:
        return [row.id for row in self.removed if row.table == table]


def _one_line(text: str) -> str:
    """The first `TEXT_MAX` characters of `text`, each line break or tab a
    space, so a row is one line of the listing."""
    return text[:TEXT_MAX].translate(str.maketrans("\n\r\t", "   "))


def plan(conn: Connection, item_id: UUID) -> Plan:
    """What purging the entry `item_id` is a version of removes, for the
    transaction's user. Locks the items and the linked events, so nothing can
    come to rest on them before the transaction ends. NotFound when the user
    has no such item."""
    found = conn.execute(
        "SELECT lineage_id FROM items WHERE id = %s", (item_id,)
    ).fetchone()
    if found is None:
        raise NotFound
    # Every version, locked, so no new version, subject, relation or evidence
    # can name one of them until the purge ends.
    items = conn.execute(
        "SELECT id, content FROM items WHERE lineage_id = %s"
        " ORDER BY created_at, id FOR UPDATE",
        (found[0],),
    ).fetchall()
    lineage = [UUID(str(row[0])) for row in items]
    evidence = conn.execute(
        "SELECT id, kind, event_id, item_id FROM item_evidence"
        " WHERE item_id = ANY(%s) ORDER BY id",
        (lineage,),
    ).fetchall()
    subjects = conn.execute(
        "SELECT s.item_id, coalesce(s.topic_id, s.entity_id),"
        " CASE WHEN s.topic_id IS NULL THEN 'named thing ' || e.name"
        " ELSE 'topic ' || t.slug END"
        " FROM item_subjects s LEFT JOIN topics t ON t.id = s.topic_id"
        " LEFT JOIN entities e ON e.id = s.entity_id"
        " WHERE s.item_id = ANY(%s) ORDER BY 1, 2",
        (lineage,),
    ).fetchall()
    relations = conn.execute(
        "SELECT id, from_item, kind, to_item FROM item_relations"
        " WHERE from_item = ANY(%s) OR to_item = ANY(%s) ORDER BY id",
        (lineage, lineage),
    ).fetchall()
    relation_ids = [UUID(str(row[0])) for row in relations]
    # Every event linked to what goes, locked, so no other item's evidence
    # can come to rest on one, and whether anything that stays references it.
    events = conn.execute(
        "WITH linked AS ("
        "  SELECT id FROM events WHERE subject_item = ANY(%(items)s)"
        "  UNION SELECT event_id FROM item_evidence WHERE item_id = ANY(%(items)s)"
        "  UNION SELECT created_by_event FROM items WHERE id = ANY(%(items)s)"
        "  UNION SELECT created_by_event FROM item_relations"
        "   WHERE id = ANY(%(relations)s))"
        " SELECT e.id, e.evidence,"
        "  (e.subject_item IS NOT NULL AND e.subject_item <> ALL(%(items)s))"
        "  OR EXISTS (SELECT FROM item_evidence ev WHERE ev.event_id = e.id"
        "   AND ev.item_id <> ALL(%(items)s))"
        "  OR EXISTS (SELECT FROM items i WHERE i.created_by_event = e.id"
        "   AND i.id <> ALL(%(items)s))"
        "  OR EXISTS (SELECT FROM item_relations r WHERE r.created_by_event = e.id"
        "   AND r.id <> ALL(%(relations)s)),"
        "  e.subject_item = ANY(%(items)s)"
        " FROM events e WHERE e.id IN (SELECT id FROM linked)"
        " ORDER BY e.id FOR UPDATE OF e",
        {"items": lineage, "relations": relation_ids},
    ).fetchall()
    rows = (
        [Row("item_evidence", str(i), f"{kind} {item} by event {event}")
         for i, kind, event, item in evidence]
        + [Row("item_subjects", f"{item}/{subject}", _one_line(str(name)))
           for item, subject, name in subjects]
        + [Row("item_relations", str(i), f"{source} {kind} {target}")
           for i, source, kind, target in relations]
        + [Row("items", str(i), _one_line(str(content))) for i, content in items]
        + [Row("events", str(i), _one_line(str(text)))
           for i, text, stays, _ in events if not stays]
    )  # fmt: skip
    kept = [
        (Row("events", str(i), _one_line(str(text))), bool(about))
        for i, text, stays, about in events
        if stays
    ]
    return Plan(lineage, rows, kept)


def listing(account_id: UUID, item_id: UUID, plan: Plan) -> str:
    """The rows a purge removes, one per line by table and id with the first
    characters of its text, and the linked events it keeps."""
    lines = [f"Purging {item_id} from the memory of {account_id} removes:"]
    lines += [f"{row.table:<16}{row.id}  {row.text}" for row in plan.removed]
    if plan.kept:
        lines.append(
            "and keeps these events, which other entries still rest on;"
            " one marked cleared no longer names the entry:"
        )
        lines += [
            f"{row.table:<16}{row.id}  {'(cleared) ' if cleared else ''}{row.text}"
            for row, cleared in plan.kept
        ]
    return "\n".join(lines) + "\n"


def remove(conn: Connection, plan: Plan) -> None:
    """Removes what `plan` lists, in the caller's transaction, and clears the
    subject_item of each kept event that named a removed item. Each table's
    rows go by id, so what goes is exactly what was listed."""
    conn.execute(
        sql.SQL("SET CONSTRAINTS {} DEFERRED").format(sql.Identifier(DEFERRED))
    )
    _delete(conn, "item_evidence", plan.ids("item_evidence"))
    subjects = [row.split("/") for row in plan.ids("item_subjects")]
    deleted = conn.execute(
        "DELETE FROM item_subjects s USING unnest(%s::uuid[], %s::uuid[])"
        " AS gone(item_id, subject_id) WHERE s.item_id = gone.item_id"
        " AND coalesce(s.topic_id, s.entity_id) = gone.subject_id",
        ([item for item, _ in subjects], [subject for _, subject in subjects]),
    )
    _check(deleted.rowcount, len(subjects), "item_subjects")
    _delete(conn, "item_relations", plan.ids("item_relations"))
    cleared = [row.id for row, about in plan.kept if about]
    updated = conn.execute(
        "UPDATE events SET subject_item = NULL WHERE id = ANY(%s::uuid[])", (cleared,)
    )
    _check(updated.rowcount, len(cleared), "events cleared")
    _delete(conn, "items", plan.ids("items"))
    _delete(conn, "events", plan.ids("events"))
    # Checked here rather than at commit, so a failure is the purge's.
    conn.execute(
        sql.SQL("SET CONSTRAINTS {} IMMEDIATE").format(sql.Identifier(DEFERRED))
    )


def _delete(conn: Connection, table: str, ids: Sequence[str]) -> None:
    deleted = conn.execute(
        sql.SQL("DELETE FROM {} WHERE id = ANY(%s::uuid[])").format(
            sql.Identifier(table)
        ),
        (list(ids),),
    )
    _check(deleted.rowcount, len(ids), table)


def _check(count: int, expected: int, what: str) -> None:
    """Under the plan's locks the counts always agree; if they ever do not,
    the transaction is rolled back rather than removing other than listed."""
    if count != expected:
        raise psycopg.DataError(f"{what}: {count} rows changed, {expected} listed")


def _counts(plan: Plan) -> str:
    counts = [(table, len(plan.ids(table))) for table in TABLES]
    return ", ".join(f"{count} {table}" for table, count in counts if count)


def run(
    conn: Connection,
    account_id: UUID,
    item_id: UUID,
    *,
    stdin: TextIO,
    stdout: TextIO,
    stderr: TextIO,
) -> int:
    """The command, on the owner's `conn`: in one transaction for
    `account_id`, it lists what purging `item_id` removes, asks, reads one
    line from `stdin`, and removes it all only for the exact word yes."""
    try:
        with conn.transaction():
            conn.execute(
                "SELECT set_config(%s, %s, true)", (db.USER_SETTING, str(account_id))
            )
            try:
                found = plan(conn, item_id)
            except NotFound:
                print(
                    f"there is no item {item_id} in the memory of {account_id},"
                    " nothing was removed",
                    file=stderr,
                )
                return 1
            stdout.write(listing(account_id, item_id, found))
            stdout.write(
                f"Type yes to remove these {len(found.removed)} rows for good: "
            )
            stdout.flush()
            line = stdin.readline()
            answer = line.removesuffix("\n").removesuffix("\r")
            if answer != YES:
                # A line typed at the terminal ends the prompt's line itself.
                stdout.write(
                    ("" if line.endswith("\n") else "\n") + "nothing was removed\n"
                )
                raise psycopg.Rollback
            remove(conn, found)
    except psycopg.Error as err:
        # psycopg names the host, the role and the reason, never the password.
        print(f"purge failed, nothing was removed: {err}", file=stderr)
        return 1
    if answer != YES:
        return 1
    print(
        f"removed {len(found.removed)} rows for good, the ones listed above:"
        f" {_counts(found)}",
        file=stdout,
    )
    return 0


def main(account_id: UUID, item_id: UUID) -> int:
    """`brain purge <account-id> <item-id>`, as the owner, from
    BRAIN_OWNER_DATABASE_URL, on the terminal's stdin and stdout."""
    url = os.environ.get("BRAIN_OWNER_DATABASE_URL")
    if not url:
        print(
            "BRAIN_OWNER_DATABASE_URL is not set, see Brain/README.md", file=sys.stderr
        )
        return 1
    try:
        conn = psycopg.connect(url)
    except psycopg.Error as err:
        print(f"could not connect to the database as the owner: {err}", file=sys.stderr)
        return 1
    with conn:
        return run(
            conn,
            account_id,
            item_id,
            stdin=sys.stdin,
            stdout=sys.stdout,
            stderr=sys.stderr,
        )
