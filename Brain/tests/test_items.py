"""The memory store: items, their subjects, relations and evidence, written
through the API as profile entries and rules, on a database of the test's
own, as brain_app. The tables and which kind goes in which store are in
`Brain/docs/data-model.md`; why a hand made entry needs no confirming is
"Standing rules are created deliberately" in `Brain/docs/trust-model.md`."""

import uuid
from collections.abc import Callable
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


def test_a_profile_entry_is_stored_as_an_asserted_claim_in_profile(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    before = datetime.now(UTC)

    written = write(alice, profile(content="  Wants to become an expert engineer.\n"))

    item_id = uuid.UUID(written["id"])
    assert item_id.version == 7
    assert written["lineage_id"] == written["id"]
    assert written["kind"] == "profile"
    assert written["store"] == "profile"
    assert written["nature"] == "claim"
    assert written["provenance"] == "asserted"
    assert written["status"] == "active"
    assert written["inactive_reason"] is None
    assert written["category"] == "values"
    # Surrounding whitespace dropped, nothing else.
    assert written["content"] == "Wants to become an expert engineer."
    assert written["value"] is None
    assert written["details"] == {}
    assert written["occurred_at"] is None and written["occurred_precision"] is None
    assert written["topics"] == [] and written["entities"] == []
    assert before <= datetime.fromisoformat(written["created_at"]) <= datetime.now(UTC)
    # As stored, past the API.
    with admin_connection(database) as conn:
        row = conn.execute(
            "SELECT user_id, kind, store, nature, provenance, status, lineage_id"
            " FROM items WHERE id = %s",
            (item_id,),
        ).fetchone()
    assert row == (ALICE, "profile", "profile", "claim", "asserted", "active", item_id)


@pytest.mark.parametrize("category", CATEGORIES)
def test_every_profile_category_is_accepted(
    category: str, calling: Callable[[uuid.UUID], Caller]
) -> None:
    assert write(calling(ALICE), profile(category=category))["category"] == category


@pytest.mark.parametrize(
    "category", ["skills", "Values", "working-style", "", None, ...]
)
def test_a_profile_entry_needs_a_known_category(
    category: Any, calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    response = calling(ALICE).post(ITEMS, profile(category=category))

    assert response.status_code == 422, response.text
    assert counts(database)["items"] == 0


def test_a_rule_is_stored_in_procedural_and_is_global(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    assert alice.post(TOPICS, {"name": "Rust"}).status_code == 201

    written = write(alice, rule())

    assert written["kind"] == "rule"
    assert written["store"] == "procedural"
    assert written["nature"] == "claim"
    assert written["provenance"] == "asserted"
    assert written["category"] is None
    assert written["topics"] == [] and written["entities"] == []
    for subjects in [{"topics": ["rust"]}, {"entities": ["Personal Platform"]}]:
        refused = alice.post(ITEMS, rule(**subjects))
        assert refused.status_code == 422, refused.text
    # A rule has no category either.
    assert alice.post(ITEMS, rule(category="values")).status_code == 422
    assert counts(database)["items"] == 1
    assert counts(database)["item_subjects"] == 0


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


@pytest.mark.parametrize(
    "body",
    [
        profile(content=""),
        profile(content=" \n\t "),
        profile(content="x" * 4001),
        profile(content="nul \u0000 inside"),
        profile(content=...),
        profile(kind="state"),
        profile(kind=...),
        profile(topics=["Not A Slug"]),
        profile(feeling="unknown field"),
        rule(content=""),
    ],
    ids=[
        "empty",
        "whitespace only",
        "over 4000",
        "nul",
        "content missing",
        "kind not accepted yet",
        "kind missing",
        "malformed slug",
        "unknown field",
        "empty rule",
    ],
)
def test_a_malformed_entry_is_refused(
    body: dict[str, Any], calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    response = calling(ALICE).post(ITEMS, body)

    assert response.status_code == 422, response.text
    assert counts(database)["items"] == 0
    assert counts(database)["events"] == 0


def test_content_of_exactly_4000_characters_is_stored(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    content = "y" * 4000
    assert write(calling(ALICE), rule(content=content))["content"] == content


def test_writing_an_entry_records_an_asserted_event_in_the_same_transaction(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    content = "Mental model first, then structure, then code."

    written = write(alice, rule(content=content))

    [event] = alice.get(EVENTS).json()
    assert event["verb"] == "asserted"
    assert event["origin"] == "brain"
    assert event["source"] == "manual-entry"
    assert event["evidence"] == f'Entered by hand as rule: "{content}"'
    assert event["occurred_at"] == event["recorded_at"]
    assert event["occurred_precision"] == "instant"
    assert event["payload"]["item"] == written["id"]
    assert written["created_by_event"] == event["id"]
    with admin_connection(database) as conn:
        evidence = conn.execute(
            "SELECT user_id, item_id, event_id, kind FROM item_evidence"
        ).fetchall()
        # The item and its event were made by one transaction.
        same = conn.execute(
            "SELECT i.xmin = e.xmin FROM items i"
            " JOIN events e ON e.id = i.created_by_event"
        ).fetchone()
    assert evidence == [
        (ALICE, uuid.UUID(written["id"]), uuid.UUID(event["id"]), "supports")
    ]
    assert same == (True,)


def test_an_entry_may_say_when_it_became_true(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)

    write(
        alice,
        profile(occurred_at="2025-09-01T00:00:00+00:00", occurred_precision="month"),
    )

    [event] = alice.get(EVENTS).json()
    assert datetime.fromisoformat(event["occurred_at"]) == datetime(
        2025, 9, 1, tzinfo=UTC
    )
    assert event["occurred_precision"] == "month"
    assert event["recorded_at"] != event["occurred_at"]


def test_the_evidence_of_a_long_entry_is_cut_to_500_characters(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    content = "z" * 4000

    written = write(alice, profile(content=content))

    [event] = alice.get(EVENTS).json()
    assert len(event["evidence"]) == 500
    assert event["evidence"].startswith('Entered by hand as profile: "zzz')
    # It still reads as a quotation, and says it was cut.
    assert event["evidence"].endswith('z…"')
    assert written["content"] == content


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
