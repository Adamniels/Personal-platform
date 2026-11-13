"""`brain export` and `brain import`, the memory file, against a real Postgres:
each test on a database of its own, as brain_app through the door, and an
import into a second, fresh copy of the migrated template where it needs one.
The format is "The memory file" in `Brain/docs/data-model.md`, and the
commands are "Export and import" in `Brain/README.md`."""

import copy
import json
import os
import stat
import subprocess
import sys
import uuid
from collections.abc import AsyncIterator, Callable, Iterator
from contextlib import asynccontextmanager
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import psycopg
import pytest
from psycopg import sql
from psycopg_pool import AsyncConnectionPool

from brain import db, memory_file
from brain.memory_file import FileRefused
from tests.conftest import Caller
from tests.harness import (
    admin_connection,
    app_url,
    create_database,
    drop_database,
    unique_name,
)

ALICE = uuid.UUID("0190a8f0-0000-7000-8000-00000000a11c")
BOB = uuid.UUID("0190a8f0-0000-7000-8000-000000000b0b")
CAROL = uuid.UUID("0190a8f0-0000-7000-8000-0000000ca201")

# The clock the commands would read, fixed, so two exports can be compared.
AT = datetime(2026, 11, 13, 12, 0, tzinfo=UTC)

API = "/brain/api"
BRAIN_COMMAND = Path(sys.executable).parent / "brain"


@pytest.fixture
def fresh_database(template: str) -> Iterator[str]:
    """A second copy of the migrated template, empty, to import into."""
    name = unique_name("test")
    create_database(name, template=template)
    yield name
    drop_database(name)


@asynccontextmanager
async def opened(database: str) -> AsyncIterator[AsyncConnectionPool]:
    pool = AsyncConnectionPool(app_url(database), min_size=1, max_size=1, open=False)
    await pool.open(wait=True)
    try:
        yield pool
    finally:
        await pool.close()


@pytest.fixture
async def fresh_pool(fresh_database: str) -> AsyncIterator[AsyncConnectionPool]:
    async with opened(fresh_database) as pool:
        yield pool


def rows(database: str) -> dict[str, list[tuple[object, ...]]]:
    """Every row of the eight tables, every column the file holds, as the
    superuser sees them, past row level security, in the file's order."""
    found: dict[str, list[tuple[object, ...]]] = {}
    with admin_connection(database) as conn:
        for table, columns in memory_file.TABLES.items():
            query = sql.SQL("SELECT {} FROM {} ORDER BY {}").format(
                sql.SQL(", ").join(map(sql.Identifier, columns)),
                sql.Identifier(table),
                sql.SQL(memory_file.ORDER[table]),
            )
            found[table] = conn.execute(query).fetchall()
    return found


def total(database: str) -> int:
    return sum(len(table) for table in rows(database).values())


# A small memory written out by hand, every time in the past and every id
# fixed, so a test can see exactly what an import keeps: one topic and an
# alias of it, a named thing, the asserted event that made a profile entry,
# the entry about the topic, and the evidence linking the two.
# The alias is minted before its target, so it comes first in the file and
# in the insert, and the reference is checked only once the table is in.
ALIAS = "0190a8f0-0001-7000-8000-000000000001"
TOPIC = "0190a8f0-0001-7000-8000-000000000002"
ENTITY = "0190a8f0-0002-7000-8000-000000000001"
EVENT = "0190a8f0-0003-7000-8000-000000000001"
ITEM = "0190a8f0-0004-7000-8000-000000000001"
EVIDENCE = "0190a8f0-0005-7000-8000-000000000001"


def small_memory(account: uuid.UUID = ALICE) -> dict[str, Any]:
    user = str(account)
    return {
        "format": 1,
        "exported_at": "2024-06-01T08:00:00+00:00",
        "account_id": user,
        "topics": [
            {
                "id": ALIAS,
                "user_id": user,
                "slug": "rust-lang",
                "name": "Rust lang",
                "state": "canonical",
                "alias_of": TOPIC,
                "created_at": "2024-03-02T10:00:00+00:00",
            },
            {
                "id": TOPIC,
                "user_id": user,
                "slug": "rust",
                "name": "Rust",
                "state": "canonical",
                "alias_of": None,
                "created_at": "2024-03-01T09:30:00.123456+00:00",
            },
        ],
        "topic_relations": [],
        "entities": [
            {
                "id": ENTITY,
                "user_id": user,
                "name": "Platform",
                "label": "project",
                "created_at": "2024-03-01T09:31:00+00:00",
            }
        ],
        "events": [
            {
                "id": EVENT,
                "user_id": user,
                "verb": "asserted",
                "subject_topic": None,
                "subject_entity": None,
                "subject_item": None,
                "origin": "brain",
                "source": "manual-entry",
                "run": None,
                "occurred_at": "2024-03-03T11:00:00+00:00",
                "occurred_precision": "instant",
                "recorded_at": "2024-03-03T11:00:00+00:00",
                "evidence": 'Entered by hand as profile: "I like Rust"',
                "payload": {"item": ITEM, "kind": "profile", "category": "interests"},
                "payload_version": 1,
                "idempotency_key": f"item:{ITEM}",
            }
        ],
        "items": [
            {
                "id": ITEM,
                "user_id": user,
                "lineage_id": ITEM,
                "kind": "profile",
                "store": "profile",
                "nature": "claim",
                "provenance": "asserted",
                "content": "I like Rust",
                "value": None,
                "category": "interests",
                "details": {},
                "occurred_at": None,
                "occurred_precision": None,
                "status": "active",
                "inactive_reason": None,
                "created_at": "2024-03-03T11:00:00+00:00",
                "created_by_event": EVENT,
            }
        ],
        "item_subjects": [
            {"item_id": ITEM, "user_id": user, "topic_id": TOPIC, "entity_id": None}
        ],
        "item_relations": [],
        "item_evidence": [
            {
                "id": EVIDENCE,
                "user_id": user,
                "item_id": ITEM,
                "event_id": EVENT,
                "kind": "supports",
                "created_at": "2024-03-03T11:00:00+00:00",
            }
        ],
    }


def text(memory: dict[str, Any]) -> str:
    return json.dumps(memory, indent=2) + "\n"


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
    other, an entry retired and one restored, a claim confirmed, a related
    topic, and two contradictions between claims, one of them taken back."""
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
    post(caller, f"topics/{systems['id']}/related", {"related_id": rust["id"]})
    mornings = post(caller, "items", {"kind": "state", "content": "Works mornings"})
    evenings = post(caller, "items", {"kind": "state", "content": "Works evenings"})
    post(
        caller,
        f"items/{mornings['id']}/contradicts",
        {"contradicts": evenings["id"]},
    )
    taken_back = post(
        caller,
        f"items/{restored['id']}/contradicts",
        {"contradicts": state["id"]},
    )
    post(caller, f"item-relations/{taken_back['id']}/end", None, 200)
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


# Import


async def test_import_keeps_every_id_and_every_time(
    fresh_database: str, fresh_pool: AsyncConnectionPool
) -> None:
    memory = small_memory()

    await memory_file.load(fresh_pool, memory_file.parse(text(memory)))

    imported = rows(fresh_database)
    alias, topic = imported["topics"]
    assert [str(alias[0]), str(topic[0])] == [ALIAS, TOPIC]
    assert topic[-1] == datetime(2024, 3, 1, 9, 30, 0, 123456, tzinfo=UTC)
    assert str(alias[5]) == TOPIC
    event = imported["events"][0]
    assert (str(event[0]), event[11]) == (
        EVENT,
        datetime(2024, 3, 3, 11, 0, tzinfo=UTC),
    )
    item = imported["items"][0]
    assert (str(item[0]), str(item[2]), str(item[-1])) == (ITEM, ITEM, EVENT)
    assert str(imported["item_evidence"][0][0]) == EVIDENCE
    # Nothing was given today's date, and nothing a new id.
    today = datetime.now(UTC).date()
    for found in imported.values():
        for row in found:
            assert all(
                not (isinstance(value, datetime) and value.date() == today)
                for value in row
            )
    for table in memory_file.TABLES:
        key = "item_id" if table == "item_subjects" else "id"
        assert [str(row[0]) for row in imported[table]] == [
            row[key] for row in memory[table]
        ], table


async def test_import_refuses_a_brain_that_already_holds_something_for_that_user(
    fresh_database: str, fresh_pool: AsyncConnectionPool, tmp_path: Path
) -> None:
    await memory_file.load(fresh_pool, memory_file.parse(text(small_memory())))
    before = rows(fresh_database)
    # Only an entity, with a new id and name, so nothing but the rule refuses it.
    other = {**small_memory(), **{t: [] for t in memory_file.TABLES}}
    other["entities"] = [
        {
            "id": str(uuid.uuid7()),
            "user_id": str(ALICE),
            "name": "Something else",
            "label": None,
            "created_at": "2024-01-01T00:00:00+00:00",
        }
    ]
    file = tmp_path / "other.json"
    file.write_text(text(other))

    done = run_command(fresh_database, "import", str(file))

    assert done.returncode != 0
    assert "already holds" in done.stderr
    assert rows(fresh_database) == before


async def test_import_is_all_or_nothing(
    fresh_database: str, fresh_pool: AsyncConnectionPool
) -> None:
    memory = small_memory()
    # The last table, so every one before it was inserted when it fails.
    memory["item_evidence"][0]["kind"] = "endorses"

    with pytest.raises(FileRefused, match="item_evidence"):
        await memory_file.load(fresh_pool, memory_file.parse(text(memory)))

    assert total(fresh_database) == 0


async def test_import_refuses_a_file_from_a_newer_format(
    fresh_database: str, fresh_pool: AsyncConnectionPool
) -> None:
    newer = {**small_memory(), "format": 2, "something_new": []}

    with pytest.raises(FileRefused, match="format 2.*format 1"):
        memory_file.parse(text(newer))
    assert total(fresh_database) == 0


@pytest.mark.parametrize(
    ("change", "named"),
    [
        (lambda m: "{not json", "not JSON"),
        (lambda m: "[]", "not a JSON object"),
        (lambda m: text({k: v for k, v in m.items() if k != "format"}), "format"),
        (lambda m: text({**m, "format": "1"}), "format"),
        (lambda m: text({**m, "format": True}), "format"),
        (lambda m: text({**m, "format": 0}), "format"),
        (lambda m: text({**m, "account_id": "alice"}), "account_id"),
        (lambda m: text({**m, "exported_at": "yesterday"}), "exported_at"),
        (lambda m: text({**m, "exported_at": "2024-06-01T08:00:00"}), "exported_at"),
        (lambda m: text({k: v for k, v in m.items() if k != "items"}), "items"),
        (lambda m: text({**m, "notes": []}), "notes"),
        (lambda m: text({**m, "topics": {}}), "topics"),
        (lambda m: text({**m, "topics": [1]}), "topics row 1"),
        (
            lambda m: text({**m, "entities": [{**m["entities"][0], "colour": "red"}]}),
            "entities row 1.*colour",
        ),
        (
            lambda m: text(
                {
                    **m,
                    "entities": [
                        {k: v for k, v in m["entities"][0].items() if k != "label"}
                    ],
                }
            ),
            "entities row 1.*label",
        ),
        (
            lambda m: text(
                {**m, "entities": [{**m["entities"][0], "user_id": str(BOB)}]}
            ),
            "entities row 1.*another account",
        ),
        (
            lambda m: '{"format": 1, "format": 1}',
            "format.*twice",
        ),
    ],
)
async def test_import_refuses_a_malformed_file(
    fresh_database: str,
    change: Callable[[dict[str, Any]], str],
    named: str,
) -> None:
    with pytest.raises(FileRefused, match=named):
        memory_file.parse(change(small_memory()))
    assert total(fresh_database) == 0


def test_the_import_command_names_what_is_wrong_and_exits_non_zero(
    fresh_database: str, tmp_path: Path
) -> None:
    file = tmp_path / "broken.json"
    file.write_text("{not json")

    done = run_command(fresh_database, "import", str(file))

    assert done.returncode == 1
    assert "not JSON" in done.stderr
    assert total(fresh_database) == 0
    missing = run_command(fresh_database, "import", str(tmp_path / "nowhere.json"))
    assert missing.returncode == 1
    assert "nowhere.json" in missing.stderr


async def test_import_refuses_a_verb_the_brain_does_not_know(
    fresh_database: str, fresh_pool: AsyncConnectionPool
) -> None:
    memory = small_memory()
    memory["events"][0]["verb"] = "pondered"

    with pytest.raises(FileRefused, match="pondered"):
        await memory_file.load(fresh_pool, memory_file.parse(text(memory)))
    assert total(fresh_database) == 0


async def test_import_into_another_account_rewrites_only_the_user(
    fresh_database: str, fresh_pool: AsyncConnectionPool
) -> None:
    memory = small_memory(ALICE)

    await memory_file.load(
        fresh_pool, memory_file.parse(text(memory)), as_account=CAROL
    )

    as_carol = json.loads(await memory_file.export(fresh_pool, CAROL, exported_at=AT))
    expected = copy.deepcopy(memory)
    expected["account_id"] = str(CAROL)
    for table in memory_file.TABLES:
        for row in expected[table]:
            row["user_id"] = str(CAROL)
    expected["exported_at"] = as_carol["exported_at"]
    # The file's times, as the export writes them back: the same instants.
    for table in memory_file.TABLES:
        for got, want in zip(as_carol[table], expected[table], strict=True):
            for column, value in want.items():
                if isinstance(value, str) and value.startswith("2024-"):
                    assert datetime.fromisoformat(
                        got[column]
                    ) == datetime.fromisoformat(value)
                    want[column] = got[column]
    assert as_carol == expected
    assert all(
        row["user_id"] == str(CAROL)
        for table in memory_file.TABLES
        for row in as_carol[table]
    )
    as_alice = json.loads(await memory_file.export(fresh_pool, ALICE, exported_at=AT))
    assert all(as_alice[table] == [] for table in memory_file.TABLES)


async def test_two_imports_for_one_account_at_once_import_once(
    fresh_database: str, fresh_pool: AsyncConnectionPool
) -> None:
    """The second waits on the account's import lock, then finds the memory
    the first wrote, and refuses."""
    async with opened(fresh_database) as second:
        async with db.transaction(fresh_pool, ALICE) as first:
            await memory_file.load_in(
                first, memory_file.parse(text(small_memory())), ALICE
            )
            other = {**small_memory(), **{t: [] for t in memory_file.TABLES}}
            # Held by the first, still open: the second cannot get past it.
            with pytest.raises(psycopg.errors.LockNotAvailable):
                async with db.transaction(second, ALICE) as conn:
                    await conn.execute("SET LOCAL lock_timeout = '200ms'")
                    await memory_file.load_in(
                        conn, memory_file.parse(text(other)), ALICE
                    )
        with pytest.raises(FileRefused, match="already holds"):
            await memory_file.load(second, memory_file.parse(text(other)))
    assert len(rows(fresh_database)["topics"]) == 2


# The round trip


async def test_export_then_import_gives_back_the_same_memory(
    database: str,
    pool: AsyncConnectionPool,
    fresh_database: str,
    fresh_pool: AsyncConnectionPool,
    calling: Callable[[uuid.UUID], Caller],
    tmp_path: Path,
) -> None:
    a_whole_memory(calling(ALICE))
    a_whole_memory(calling(BOB))
    first = await memory_file.export(pool, ALICE, exported_at=AT)
    file = tmp_path / "memory.json"
    file.write_text(first)

    imported = run_command(fresh_database, "import", str(file))
    assert imported.returncode == 0, imported.stderr

    second = await memory_file.export(fresh_pool, ALICE, exported_at=AT)
    assert second == first
    # And it was a whole memory: every table held something to compare.
    document = json.loads(first)
    assert all(document[table] for table in memory_file.TABLES)
    assert any(row["alias_of"] for row in document["topics"])
    assert any(row["ended_at"] for row in document["topic_relations"])
    assert {row["inactive_reason"] for row in document["items"]} >= {
        "superseded",
        "retired",
    }
    assert {row["kind"] for row in document["items"]} == {
        "profile",
        "rule",
        "state",
        "topic_level",
        "topic_focus",
        "history",
        "decision",
    }
    assert any(row["subject_item"] for row in document["events"])
    # What conflicts and related topics added: the verb marking makes, a
    # related topic, and a contradiction that holds beside one taken back.
    assert any(row["verb"] == "contradicted" for row in document["events"])
    assert any(row["kind"] == "related_to" for row in document["topic_relations"])
    contradictions = [
        row for row in document["item_relations"] if row["kind"] == "contradicts"
    ]
    assert {row["ended_at"] is None for row in contradictions} == {True, False}
    # Only Alice's rows reached the fresh brain.
    assert {
        str(row[1]) for table in rows(fresh_database).values() for row in table
    } == {str(ALICE)}
