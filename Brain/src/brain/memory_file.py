"""The memory file: one account's whole memory as one JSON document, written
by `brain export` and read back into an empty brain by `brain import`. The
format is "The memory file" in docs/data-model.md, and the commands are
"Export and import" in Brain/README.md.

Both run as brain_app, through the door, for the one account, so row level
security holds for them as for any request: the export reads no row of
another user because it cannot, and the import writes none because the
policy refuses it.

The file is made to be compared: rows in a fixed order, keys sorted, times
in UTC, so two exports of the same memory at the same `exported_at` are
equal byte for byte, and an export, an import into a fresh brain and an
export again give back the file it started from.
"""

import asyncio
import json
import os
import sys
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from uuid import UUID

import psycopg
from psycopg import sql

from brain import db

# The version of the format below. A file of a newer one is refused, since
# what it holds may not fit this brain's tables; a change to the tables or
# their columns is a new version, which `TABLES` makes hard to miss, since a
# test compares it with the schema.
FORMAT = 1

# The tables the file holds, in the order the import inserts them, and each
# one's columns, in the table's own order: every stored column, and none of
# the generated ones, which Postgres computes again. `verbs` is not here: it
# is global and comes from the migrations, and `schema_migrations` is the
# owner's.
TABLES: dict[str, tuple[str, ...]] = {
    "topics": ("id", "user_id", "slug", "name", "state", "alias_of", "created_at"),
    "topic_relations": (
        "id",
        "user_id",
        "from_topic",
        "to_topic",
        "kind",
        "created_at",
        "ended_at",
    ),
    "entities": ("id", "user_id", "name", "label", "created_at"),
    "events": (
        "id",
        "user_id",
        "verb",
        "subject_topic",
        "subject_entity",
        "subject_item",
        "origin",
        "source",
        "run",
        "occurred_at",
        "occurred_precision",
        "recorded_at",
        "evidence",
        "payload",
        "payload_version",
        "idempotency_key",
    ),
    "items": (
        "id",
        "user_id",
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
    ),
    "item_subjects": ("item_id", "user_id", "topic_id", "entity_id"),
    "item_relations": (
        "id",
        "user_id",
        "from_item",
        "to_item",
        "kind",
        "created_at",
        "ended_at",
        "created_by_event",
    ),
    "item_evidence": ("id", "user_id", "item_id", "event_id", "kind", "created_at"),
}

# How each table's rows are ordered in the file: by id, and item_subjects,
# which has none, by the item and then the subject, which is unique per item.
ORDER: dict[str, str] = {table: "id" for table in TABLES} | {
    "item_subjects": "item_id, topic_id, entity_id"
}


def _export_query() -> sql.Composed:
    """One statement for all eight tables, so the file is one snapshot: a
    write landing in the middle of an export is either all in it or not."""
    columns = [
        sql.SQL(
            "(SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY {order}), '[]'::jsonb)"
            " FROM (SELECT {columns} FROM {table}) AS r)"
        ).format(
            order=sql.SQL(ORDER[table]),
            columns=sql.SQL(", ").join(map(sql.Identifier, names)),
            table=sql.Identifier(table),
        )
        for table, names in TABLES.items()
    ]
    return sql.SQL("SELECT {}").format(sql.SQL(", ").join(columns))


async def export(pool: db.Pool, account_id: UUID, *, exported_at: datetime) -> str:
    """The memory file for `account_id`, as text, stamped `exported_at`, which
    the caller passes so that the file depends on nothing else."""
    if exported_at.tzinfo is None:
        raise ValueError("exported_at must carry a zone")
    async with db.transaction(pool, account_id) as conn:
        # Times as UTC, whatever the server's zone, for the transaction alone.
        await conn.execute("SET LOCAL TimeZone TO 'UTC'")
        cursor = await conn.execute(_export_query())
        found = await cursor.fetchone()
    assert found is not None
    document: dict[str, Any] = {
        "format": FORMAT,
        "exported_at": exported_at.astimezone(UTC).isoformat(),
        "account_id": str(account_id),
    }
    for table, rows in zip(TABLES, found, strict=True):
        document[table] = [_sorted(row) for row in rows]
    return json.dumps(document, indent=2, ensure_ascii=False) + "\n"


def _sorted(value: Any) -> Any:
    """`value` with the keys of every object in it sorted, nested ones in a
    payload or details included."""
    if isinstance(value, dict):
        return {key: _sorted(value[key]) for key in sorted(value)}
    if isinstance(value, list):
        return [_sorted(item) for item in value]
    return value


def _database_url() -> str | None:
    url = os.environ.get("BRAIN_APP_DATABASE_URL")
    if not url:
        print("BRAIN_APP_DATABASE_URL is not set, see Brain/README.md", file=sys.stderr)
        return None
    return url


async def _exported(url: str, account_id: UUID, exported_at: datetime) -> str:
    pool = await db.open_pool(url)
    try:
        return await export(pool, account_id, exported_at=exported_at)
    finally:
        await pool.close()


def export_main(account_id: UUID, file: Path) -> int:
    """`brain export <account-id> <file>`: writes the file, readable by its
    owner alone, and never over one that exists."""
    if file.exists():
        print(
            f"{file} already exists, export never writes over a file", file=sys.stderr
        )
        return 1
    url = _database_url()
    if url is None:
        return 1
    try:
        text = asyncio.run(_exported(url, account_id, datetime.now(UTC)))
    except psycopg.Error as err:
        # psycopg names the host, the role and the reason, never the password.
        print(f"export failed, no file was written: {err}", file=sys.stderr)
        return 1
    try:
        handle = os.open(file, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except OSError as err:
        print(f"could not write {file}: {err.strerror}", file=sys.stderr)
        return 1
    with os.fdopen(handle, "w", encoding="utf-8") as out:
        out.write(text)
    rows = sum(len(json.loads(text)[table]) for table in TABLES)
    print(
        f"wrote {rows} rows to {file}: the whole memory of {account_id} in plain text,"
        " keep it somewhere private"
    )
    return 0
