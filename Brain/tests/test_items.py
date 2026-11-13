"""The memory store: items, their subjects, relations and evidence, written
through the API as profile entries and rules, on a database of the test's
own, as brain_app. The tables and which kind goes in which store are in
`Brain/docs/data-model.md`; why a hand made entry needs no confirming is
"Standing rules are created deliberately" in `Brain/docs/trust-model.md`."""

import uuid
from datetime import UTC, datetime
from typing import Any

import psycopg
import pytest
from psycopg_pool import AsyncConnectionPool

from brain import db, events, items
from brain.errors import Refused
from tests.conftest import Caller
from tests.harness import admin_connection

ALICE = uuid.UUID("0190a8f0-0000-7000-8000-00000000a11c")
BOB = uuid.UUID("0190a8f0-0000-7000-8000-000000000b0b")

ITEMS = "/brain/api/items"
EVENTS = "/brain/api/events"
TOPICS = "/brain/api/topics"
ENTITIES = "/brain/api/entities"

CATEGORIES = [
    "identity",
    "goals",
    "values",
    "interests",
    "preferences",
    "working_style",
]


def profile(**overrides: Any) -> dict[str, Any]:
    body: dict[str, Any] = {
        "kind": "profile",
        "category": "values",
        "content": "Correctness over agreement: push back when it is warranted.",
    }
    body.update(overrides)
    return {key: value for key, value in body.items() if value is not ...}


def rule(**overrides: Any) -> dict[str, Any]:
    body: dict[str, Any] = {
        "kind": "rule",
        "content": "Pair every question with a concrete proposal.",
    }
    body.update(overrides)
    return {key: value for key, value in body.items() if value is not ...}


def write(caller: Caller, body: dict[str, Any]) -> dict[str, Any]:
    response = caller.post(ITEMS, body)
    assert response.status_code == 201, response.text
    written: dict[str, Any] = response.json()
    return written


def listed(caller: Caller, query: str = "") -> list[dict[str, Any]]:
    response = caller.get(f"{ITEMS}{query}")
    assert response.status_code == 200, response.text
    found: list[dict[str, Any]] = response.json()
    return found


def counts(database: str) -> dict[str, int]:
    """How many rows each memory table and the events hold, for every user,
    read as the superuser, past row level security."""
    tables = ["items", "item_subjects", "item_relations", "item_evidence", "events"]
    found = {}
    with admin_connection(database) as conn:
        for table in tables:
            row = conn.execute(f"SELECT count(*) FROM {table}").fetchone()
            assert row is not None and isinstance(row[0], int)
            found[table] = row[0]
    return found


async def test_a_rule_with_a_subject_is_refused_below_the_api(
    pool: AsyncConnectionPool,
) -> None:
    # The rule is the store's, not only the request model's.
    new = items.NewItem(
        kind="rule",
        content="Concise prose.",
        category=None,
        topics=(),
        entities=("Personal Platform",),
        occurred_at=None,
        occurred_precision="instant",
    )
    with pytest.raises(Refused, match="global"):
        async with db.transaction(pool, ALICE) as conn:
            await items.write(conn, new, now=datetime.now(UTC))


async def test_a_failed_write_leaves_neither_an_item_nor_an_event(
    pool: AsyncConnectionPool, database: str, monkeypatch: pytest.MonkeyPatch
) -> None:
    class Boom(Exception):
        pass

    async def fails(*args: object, **kwargs: object) -> None:
        raise Boom

    # The last step of the write fails, after the event, the named thing it
    # names, the item and its subjects are in.
    monkeypatch.setattr(items, "_link_evidence", fails)
    new = items.NewItem(
        kind="profile",
        content="Building toward strong technical roles.",
        category="goals",
        topics=(),
        entities=("Operation Rollout",),
        occurred_at=None,
        occurred_precision="instant",
    )

    with pytest.raises(Boom):
        async with db.transaction(pool, ALICE) as conn:
            await items.write(conn, new, now=datetime.now(UTC))

    assert counts(database) == {
        "items": 0,
        "item_subjects": 0,
        "item_relations": 0,
        "item_evidence": 0,
        "events": 0,
    }
    with admin_connection(database) as admin:
        assert admin.execute("SELECT count(*) FROM entities").fetchone() == (0,)


async def test_an_event_may_name_an_item_the_user_has(
    pool: AsyncConnectionPool,
) -> None:
    now = datetime.now(UTC)
    new = items.NewItem(
        kind="rule",
        content="Concise prose.",
        category=None,
        topics=(),
        entities=(),
        occurred_at=None,
        occurred_precision="instant",
    )
    async with db.transaction(pool, ALICE) as conn:
        item_id = await items.write(conn, new, now=now)

    def confirmed(key: str) -> events.NewEvent:
        return events.NewEvent(
            verb="confirmed",
            topic=None,
            entity=None,
            item=item_id,
            origin="learning",
            source=None,
            run=None,
            occurred_at=None,
            occurred_precision="instant",
            evidence="Confirmed the rule still holds.",
            payload={},
            payload_version=1,
            idempotency_key=key,
        )

    async with db.transaction(pool, ALICE) as conn:
        recorded, created = await events.record(conn, confirmed("a"), now=now)
    assert created and recorded.subject_item == item_id
    # Another user's item is not there for Bob.
    with pytest.raises(Refused, match="no such item"):
        async with db.transaction(pool, BOB) as conn:
            await events.record(conn, confirmed("b"), now=now)


# What the app role may change in the memory tables: an item's status and
# why it is inactive, nothing else. Content is never changed in place; an
# edit is a new version, see "Versions" in Brain/docs/data-model.md.
REFUSED = [
    "UPDATE items SET content = 'rewritten'",
    "UPDATE items SET kind = 'rule'",
    "UPDATE items SET store = 'knowledge'",
    "UPDATE items SET lineage_id = id",
    "UPDATE items SET created_by_event = created_by_event",
    "UPDATE item_subjects SET topic_id = NULL",
    "UPDATE item_relations SET ended_at = now()",
    "UPDATE item_evidence SET kind = 'contradicts'",
    "DELETE FROM items",
    "DELETE FROM item_subjects",
    "DELETE FROM item_relations",
    "DELETE FROM item_evidence",
]


@pytest.mark.parametrize("statement", REFUSED)
async def test_the_service_can_change_only_status_on_an_item(
    statement: str, pool: AsyncConnectionPool
) -> None:
    with pytest.raises(psycopg.errors.InsufficientPrivilege, match="permission denied"):
        async with db.transaction(pool, ALICE) as conn:
            await conn.execute(statement)


async def test_the_service_may_change_an_items_status(
    pool: AsyncConnectionPool,
) -> None:
    # The control for the test above: allowed, on no rows.
    async with db.transaction(pool, ALICE) as conn:
        await conn.execute(
            "UPDATE items SET status = 'inactive', inactive_reason = 'retired'"
        )
