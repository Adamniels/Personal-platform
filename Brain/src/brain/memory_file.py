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
from collections.abc import Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from uuid import UUID

import psycopg
from psycopg import AsyncConnection, sql
from psycopg.rows import TupleRow
from psycopg.types.json import Jsonb

from brain import db

type Connection = AsyncConnection[TupleRow]

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

# The keys of the document, in the order the file writes them.
KEYS = ("format", "exported_at", "account_id", *TABLES)

# Events go in before items, and the two name each other: an item the event
# that made it, an event about an item that item. This reference is the one
# that waits for the end of the import's transaction, see
# migrations/0006_deferrable_event_item.sql; for every other write it is
# checked at once, as before.
DEFERRED = "events_user_id_subject_item_fkey"


class FileRefused(Exception):
    """A file that cannot be imported, or a brain it cannot be imported into,
    and what is wrong. Nothing was changed."""


@dataclass(frozen=True)
class Memory:
    """A file that parsed: its account, when it was written, and its rows,
    each with exactly its table's columns."""

    account_id: UUID
    exported_at: datetime
    tables: dict[str, list[dict[str, Any]]]

    @property
    def rows(self) -> int:
        return sum(len(rows) for rows in self.tables.values())


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


def parse(text: str) -> Memory:
    """The file `text` holds, checked for its shape: the format, the account,
    the time, every table and every row's columns, each row the file's
    account's. What the values are, Postgres checks as the import inserts
    them."""
    try:
        document = json.loads(text, object_pairs_hook=_unique)
    except json.JSONDecodeError as err:
        raise FileRefused(f"not JSON: {err}") from None
    if not isinstance(document, dict):
        raise FileRefused("not a JSON object at the top, the memory file is one")
    _check_format(document)
    missing = [key for key in KEYS if key not in document]
    if missing:
        raise FileRefused(f"missing {', '.join(missing)}")
    unknown = sorted(set(document) - set(KEYS))
    if unknown:
        raise FileRefused(f"unknown {', '.join(unknown)}, not in format {FORMAT}")
    exported_at = _time(document["exported_at"])
    account_id = _account(document["account_id"], "account_id")
    tables = {
        table: _rows(document[table], table, columns, account_id)
        for table, columns in TABLES.items()
    }
    return Memory(account_id, exported_at, tables)


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    """An object, refused when one key is in it twice, which `json` would
    otherwise settle silently by keeping the last."""
    found: dict[str, Any] = {}
    for key, value in pairs:
        if key in found:
            raise FileRefused(f"{key} is given twice in one object")
        found[key] = value
    return found


def _check_format(document: dict[str, Any]) -> None:
    if "format" not in document:
        raise FileRefused("no format, the version of the file")
    version = document["format"]
    # bool is an int to Python, and true is not a version.
    if type(version) is not int or version < 1:
        raise FileRefused(f"format is {json.dumps(version)}, not a version number")
    if version > FORMAT:
        raise FileRefused(
            f"the file is format {version}, and this brain reads format {FORMAT}:"
            " import it into a brain at least as new as the one that exported it"
        )


def _time(value: Any) -> datetime:
    try:
        parsed = datetime.fromisoformat(value) if isinstance(value, str) else None
    except ValueError:
        parsed = None
    if parsed is None or parsed.tzinfo is None:
        raise FileRefused(f"exported_at is {json.dumps(value)}, not a time with a zone")
    return parsed


def _account(value: Any, where: str) -> UUID:
    if isinstance(value, str):
        try:
            return UUID(value)
        except ValueError:
            pass
    raise FileRefused(f"{where} is {json.dumps(value)}, not an account id")


def _rows(
    value: Any, table: str, columns: Sequence[str], account_id: UUID
) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        raise FileRefused(f"{table} is not a list of rows")
    for number, row in enumerate(value, start=1):
        where = f"{table} row {number}"
        if not isinstance(row, dict):
            raise FileRefused(f"{where} is not an object")
        missing = [column for column in columns if column not in row]
        if missing:
            raise FileRefused(f"{where}: missing {', '.join(missing)}")
        unknown = sorted(set(row) - set(columns))
        if unknown:
            raise FileRefused(f"{where}: unknown column {', '.join(unknown)}")
        owner = _account(row["user_id"], f"{where}'s user_id")
        if owner != account_id:
            raise FileRefused(
                f"{where} belongs to another account, {owner}, not the file's {account_id}"
            )
    return value


async def load(pool: db.Pool, memory: Memory, *, as_account: UUID | None = None) -> int:
    """Imports `memory` into the brain, under its own account or `as_account`,
    in one transaction through the door, and returns how many rows it wrote.
    Refused, changing nothing, when the account already holds anything."""
    account = as_account or memory.account_id
    async with db.transaction(pool, account) as conn:
        return await load_in(conn, memory, account)


async def load_in(conn: Connection, memory: Memory, account: UUID) -> int:
    """`load`, in the caller's transaction, which the door opened for
    `account`. Every table in `TABLES` order, each in one statement, so a
    reference within a table, an alias to its topic or a version to the first
    of its lineage, is checked once the whole table is in."""
    # Two imports for one account at once: the second waits here, then finds
    # what the first wrote. Other accounts' imports never wait for it.
    await conn.execute(
        "SELECT pg_advisory_xact_lock(hashtextextended("
        f"'brain.import:' || current_setting('{db.USER_SETTING}'), 0))"
    )
    held = await _held(conn)
    if held:
        raise FileRefused(
            f"the brain already holds memory for {account}, {held}:"
            " a file is imported only into an account that holds none"
        )
    await _check_verbs(conn, memory.tables["events"])
    await conn.execute(
        sql.SQL("SET CONSTRAINTS {} DEFERRED").format(sql.Identifier(DEFERRED))
    )
    for table, columns in TABLES.items():
        rows = [{**row, "user_id": str(account)} for row in memory.tables[table]]
        if not rows:
            continue
        names = sql.SQL(", ").join(map(sql.Identifier, columns))
        insert = sql.SQL(
            "INSERT INTO {table} ({names}) SELECT {names}"
            " FROM jsonb_populate_recordset(NULL::{table}, %s)"
        ).format(table=sql.Identifier(table), names=names)
        try:
            await conn.execute(insert, (Jsonb(rows),))
        except psycopg.Error as err:
            raise FileRefused(f"{table}: {_reason(err)}") from err
    try:
        # Checked here rather than at commit, so a failure names its table.
        await conn.execute(
            sql.SQL("SET CONSTRAINTS {} IMMEDIATE").format(sql.Identifier(DEFERRED))
        )
    except psycopg.Error as err:
        raise FileRefused(f"events: {_reason(err)}") from err
    return memory.rows


async def _held(conn: Connection) -> str:
    """What the current user already has, as `2 topics, 1 entities`, or ''."""
    counts = sql.SQL(", ").join(
        sql.SQL("(SELECT count(*) FROM {})").format(sql.Identifier(table))
        for table in TABLES
    )
    cursor = await conn.execute(sql.SQL("SELECT {}").format(counts))
    found = await cursor.fetchone()
    assert found is not None
    return ", ".join(
        f"{count} {table}" for table, count in zip(TABLES, found, strict=True) if count
    )


async def _check_verbs(conn: Connection, events: list[dict[str, Any]]) -> None:
    """Refuses an event whose verb this brain does not know. The verbs are
    global and come from the migrations, so the file does not carry them."""
    named = sorted({str(event["verb"]) for event in events})
    cursor = await conn.execute("SELECT verb FROM verbs WHERE verb = ANY(%s)", (named,))
    known = {str(row[0]) for row in await cursor.fetchall()}
    unknown = [verb for verb in named if verb not in known]
    if unknown:
        raise FileRefused(
            f"events use verbs this brain does not know: {', '.join(unknown)}"
        )


def _reason(err: psycopg.Error) -> str:
    """Postgres's own words: the message and, when it gives one, the detail,
    which names the row's key."""
    message = err.diag.message_primary or str(err)
    detail = err.diag.message_detail
    return f"{message} ({detail})" if detail else message


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


async def _loaded(url: str, memory: Memory, as_account: UUID | None) -> int:
    pool = await db.open_pool(url)
    try:
        return await load(pool, memory, as_account=as_account)
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


def import_main(file: Path, as_account: UUID | None) -> int:
    """`brain import <file> [--as <account-id>]`: all of the file or, when
    anything is wrong, none of it."""
    try:
        text = file.read_text(encoding="utf-8")
    except OSError as err:
        print(f"could not read {file}: {err.strerror}", file=sys.stderr)
        return 1
    except UnicodeDecodeError:
        print(
            f"import refused, nothing was changed: {file} is not UTF-8 text",
            file=sys.stderr,
        )
        return 1
    url = _database_url()
    if url is None:
        return 1
    try:
        memory = parse(text)
        rows = asyncio.run(_loaded(url, memory, as_account))
    except FileRefused as err:
        print(f"import refused, nothing was changed: {err}", file=sys.stderr)
        return 1
    except psycopg.Error as err:
        print(f"import failed, nothing was changed: {err}", file=sys.stderr)
        return 1
    account = as_account or memory.account_id
    print(f"imported {rows} rows from {file} into the memory of {account}")
    return 0
