"""`brain export` and `brain import`, the memory file, against a real Postgres:
each test on a database of its own, as brain_app through the door, and an
import into a second, fresh copy of the migrated template where it needs one.
The format is "The memory file" in `Brain/docs/data-model.md`, and the
commands are "Export and import" in `Brain/README.md`."""

import json
import os
import stat
import subprocess
import sys
import uuid
from collections.abc import AsyncIterator, Callable
from contextlib import asynccontextmanager
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import psycopg
import pytest
from psycopg_pool import AsyncConnectionPool

from brain import db, memory_file
from tests.conftest import Caller
from tests.harness import (
    admin_connection,
    app_url,
)

ALICE = uuid.UUID("0190a8f0-0000-7000-8000-00000000a11c")
BOB = uuid.UUID("0190a8f0-0000-7000-8000-000000000b0b")
CAROL = uuid.UUID("0190a8f0-0000-7000-8000-0000000ca201")

# The clock the commands would read, fixed, so two exports can be compared.
AT = datetime(2026, 11, 13, 12, 0, tzinfo=UTC)

API = "/brain/api"
BRAIN_COMMAND = Path(sys.executable).parent / "brain"


def run_command(database: str, *args: str) -> subprocess.CompletedProcess[str]:
    """The installed `brain` command, as brain_app on `database`."""
    environ = {**os.environ, "BRAIN_APP_DATABASE_URL": app_url(database)}
    return subprocess.run(
        [str(BRAIN_COMMAND), *args],
        env=environ,
        capture_output=True,
        text=True,
        check=False,
    )


def post(caller: Caller, path: str, body: object = None, status: int = 201) -> Any:
    response = caller.post(f"{API}/{path}", body)
    assert response.status_code == status, response.text
    return response.json()


def a_whole_memory(caller: Caller) -> None:
    """Every kind of entry and everything that can happen to one, through the
    running brain: topics with an alias and a broader topic, a relation that
    ended, a named thing, events from a feature, a provisional topic made by
    one, profile entries, a rule, a state, a level and a focus, edited, a
    misconception and a plain history entry, two decisions one replacing the
    other, an entry retired and one restored, and a claim confirmed."""
    rust = post(caller, "topics", {"name": "Rust"})
    lang = post(caller, "topics", {"name": "Rust lang"})
    post(caller, f"topics/{lang['id']}/same-as", {"target_id": rust["id"]}, 200)
    programming = post(caller, "topics", {"name": "Programming"})
    post(caller, f"topics/{rust['id']}/broader", {"broader_id": programming["id"]})
    systems = post(caller, "topics", {"name": "Systems"})
    ended = post(
        caller, f"topics/{systems['id']}/broader", {"broader_id": programming["id"]}
    )
    post(caller, f"topic-relations/{ended['id']}/end", None, 200)
    post(caller, "entities", {"name": "Platform", "label": "project"})

    profile = post(
        caller,
        "items",
        {
            "kind": "profile",
            "category": "interests",
            "content": "Likes systems programming",
            "topics": ["rust"],
            "entities": ["Platform"],
        },
    )
    post(
        caller,
        f"items/{profile['id']}/edit",
        {"kind": "profile", "content": "Loves systems programming"},
    )
    rule = post(caller, "items", {"kind": "rule", "content": "Answer in English"})
    post(caller, f"items/{rule['id']}/retire", None, 200)
    restored = post(caller, "items", {"kind": "rule", "content": "Be concise"})
    post(caller, f"items/{restored['id']}/retire", None, 200)
    post(caller, f"items/{restored['id']}/restore", None, 200)
    state = post(caller, "items", {"kind": "state", "content": "Building the brain"})
    post(caller, f"items/{state['id']}/confirm", None, 200)
    post(caller, "items", {"kind": "topic_level", "topic": "rust", "value": "working"})
    post(caller, "items", {"kind": "topic_level", "topic": "rust", "value": "solid"})
    post(caller, "items", {"kind": "topic_focus", "topic": "rust", "value": "learning"})
    post(
        caller,
        "items",
        {
            "kind": "history",
            "topic": "rust",
            "details": {
                "misconception": True,
                "believed": "Lifetimes are runtime checks",
                "correct": "They are checked at compile time",
            },
        },
    )
    post(
        caller,
        "items",
        {
            "kind": "history",
            "content": "Shipped the walking skeleton",
            "occurred_at": "2026-10-01T00:00:00+00:00",
            "occurred_precision": "day",
            "entities": ["Platform"],
        },
    )
    old = post(
        caller,
        "items",
        {
            "kind": "decision",
            "content": "Use Postgres",
            "details": {"reasoning": "One database", "rejected": ["SQLite"]},
        },
    )
    new = post(
        caller,
        "items",
        {
            "kind": "decision",
            "content": "Use Postgres with pgvector",
            "details": {"reasoning": "Search by meaning later"},
        },
    )
    post(caller, f"items/{old['id']}/replace", {"replaced_by": new["id"]}, 200)
    post(
        caller,
        "events",
        {
            "verb": "completed",
            "topic": "async-python",
            "origin": "desk",
            "source": "session",
            "run": "run-1",
            "occurred_at": "2026-11-01T09:00:00+00:00",
            "occurred_precision": "day",
            "evidence": "Finished a session on async Python, ü and 日本",
            "payload": {"minutes": 45, "nested": {"b": 1.5, "a": [1, None, "x"]}},
            "payload_version": 2,
            "idempotency_key": "session-1",
        },
    )
    post(
        caller,
        "events",
        {
            "verb": "opened",
            "item": state["id"],
            "origin": "desk",
            "evidence": "Opened the state entry",
            "payload": {},
            "payload_version": 1,
            "idempotency_key": "opened-1",
        },
    )


# The format follows the schema


def test_the_format_names_every_scoped_table_and_every_stored_column(
    database: str,
) -> None:
    """A migration that adds a table or a column fails here, so the format is
    decided again, its version with it, rather than a file quietly losing it."""
    with admin_connection(database) as conn:
        found = conn.execute(
            "SELECT table_name, column_name FROM information_schema.columns"
            " WHERE table_schema = 'public' AND is_generated = 'NEVER'"
            " AND table_name NOT IN ('schema_migrations', 'verbs')"
            " ORDER BY table_name, ordinal_position"
        ).fetchall()
    schema: dict[str, list[str]] = {}
    for table, column in found:
        schema.setdefault(str(table), []).append(str(column))
    assert schema == {t: list(c) for t, c in memory_file.TABLES.items()}
    assert list(memory_file.TABLES) == [
        "topics",
        "topic_relations",
        "entities",
        "events",
        "items",
        "item_subjects",
        "item_relations",
        "item_evidence",
    ]


# Export


async def test_export_holds_only_the_named_users_rows(
    pool: AsyncConnectionPool, calling: Callable[[uuid.UUID], Caller]
) -> None:
    a_whole_memory(calling(ALICE))
    a_whole_memory(calling(BOB))

    exported = json.loads(await memory_file.export(pool, ALICE, exported_at=AT))

    assert exported["account_id"] == str(ALICE)
    for table in memory_file.TABLES:
        assert exported[table], f"{table} is empty"
        assert {row["user_id"] for row in exported[table]} == {str(ALICE)}
    assert str(BOB) not in json.dumps(exported)


async def test_export_works_through_row_level_security(
    pool: AsyncConnectionPool,
    calling: Callable[[uuid.UUID], Caller],
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """It reads through the door alone, as brain_app, with row level security
    on, and writes no filter of its own: for a user with nothing, while
    another user holds a whole memory, every table comes back empty."""
    a_whole_memory(calling(BOB))
    door = db.transaction
    seen: list[tuple[uuid.UUID, object, object]] = []

    @asynccontextmanager
    async def watched(
        pool: db.Pool, user_id: uuid.UUID
    ) -> AsyncIterator[psycopg.AsyncConnection[Any]]:
        async with door(pool, user_id) as conn:
            cursor = await conn.execute(
                "SELECT current_user, current_setting('row_security')"
            )
            role, security = await cursor.fetchone() or (None, None)
            seen.append((user_id, role, security))
            yield conn

    monkeypatch.setattr(db, "transaction", watched)

    exported = json.loads(await memory_file.export(pool, CAROL, exported_at=AT))

    assert seen == [(CAROL, "brain_app", "on")]
    assert all(exported[table] == [] for table in memory_file.TABLES)


async def test_the_export_is_one_json_document_in_a_fixed_order(
    pool: AsyncConnectionPool, calling: Callable[[uuid.UUID], Caller]
) -> None:
    a_whole_memory(calling(ALICE))

    exported = await memory_file.export(pool, ALICE, exported_at=AT)
    document = json.loads(exported)

    assert exported.endswith("}\n")
    assert list(document) == [
        "format",
        "exported_at",
        "account_id",
        *memory_file.TABLES,
    ]
    assert document["format"] == 1
    assert document["exported_at"] == "2026-11-13T12:00:00+00:00"
    for table, columns in memory_file.TABLES.items():
        for row in document[table]:
            assert list(row) == sorted(columns)
    for table in ("topics", "events", "items", "item_evidence"):
        ids = [row["id"] for row in document[table]]
        assert ids == sorted(ids)
    # Every key sorted, nested ones in a payload too, two spaces of indent.
    assert (
        exported
        == json.dumps(document, indent=2, sort_keys=False, ensure_ascii=False) + "\n"
    )
    payloads = [e["payload"] for e in document["events"] if e["origin"] == "desk"]
    assert list(payloads[0]) == ["minutes", "nested"]
    assert list(payloads[0]["nested"]) == ["a", "b"]
    # Times in UTC, so the file does not depend on the server's zone.
    assert all(e["recorded_at"].endswith("+00:00") for e in document["events"])


def test_the_export_command_writes_a_private_file_and_says_what_it_holds(
    database: str, calling: Callable[[uuid.UUID], Caller], tmp_path: Path
) -> None:
    a_whole_memory(calling(ALICE))
    file = tmp_path / "memory.json"

    done = run_command(database, "export", str(ALICE), str(file))

    assert done.returncode == 0, done.stderr
    assert len(done.stdout.splitlines()) == 1
    assert "plain text" in done.stdout and "private" in done.stdout
    assert stat.S_IMODE(file.stat().st_mode) == 0o600
    assert json.loads(file.read_text())["account_id"] == str(ALICE)

    again = run_command(database, "export", str(ALICE), str(file))
    assert again.returncode != 0
    assert "already exists" in again.stderr

    unparsed = run_command(database, "export", "not-an-id", str(tmp_path / "x.json"))
    assert unparsed.returncode == 2
    assert "not-an-id" in unparsed.stderr
