"""What is going on now, what has happened, and what was decided, through the
API on a database of the test's own, as brain_app. Why a state goes stale and
a record never does is "Claim versus record" in
`Brain/docs/knowledge-model.md` and "Confidence, currency and status are three
different things" in `Brain/docs/trust-model.md`; why a decision is an event
and an item is "Decisions" in `Brain/docs/event-model.md`.

Currency depends on the time, so the now view is read here at a time the test
passes in, through `brain.now_and_history`, beside the routes that read it at
the request's."""

import asyncio
import uuid
from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from typing import Any

import pytest
from psycopg_pool import AsyncConnectionPool

from brain import db, items, now_and_history
from brain.errors import Conflict
from tests.conftest import Caller
from tests.harness import admin_connection, app_url
from tests.test_corrections import about, act
from tests.test_items import (
    ALICE,
    BOB,
    ITEMS,
    TOPICS,
    counts,
    profile,
    rule,
    write,
)

NOW = "/brain/api/now"
HISTORY = "/brain/api/history"


def state(**overrides: Any) -> dict[str, Any]:
    body: dict[str, Any] = {
        "kind": "state",
        "content": "Building the brain's memory store.",
    }
    body.update(overrides)
    return {key: value for key, value in body.items() if value is not ...}


def history(**overrides: Any) -> dict[str, Any]:
    body: dict[str, Any] = {
        "kind": "history",
        "content": "Took the distributed systems course.",
        "occurred_at": "2023-09-01T00:00:00Z",
        "occurred_precision": "month",
    }
    body.update(overrides)
    return {key: value for key, value in body.items() if value is not ...}


def decision(**overrides: Any) -> dict[str, Any]:
    body: dict[str, Any] = {
        "kind": "decision",
        "content": "The brain is its own service.",
        "details": {
            "reasoning": "Splitting memory across languages failed in v1.",
            "rejected": ["A memory module inside each feature"],
        },
    }
    body.update(overrides)
    return {key: value for key, value in body.items() if value is not ...}


def iso(moment: datetime) -> str:
    return moment.isoformat().replace("+00:00", "Z")


def when(text: str) -> datetime:
    return datetime.fromisoformat(text)


def now_at(database: str, user: uuid.UUID, moment: datetime) -> list[Any]:
    """The now view of `user` as it reads at `moment`, the time passed in."""

    async def read_view() -> list[Any]:
        pool = AsyncConnectionPool(
            app_url(database), min_size=1, max_size=1, open=False
        )
        await pool.open(wait=True)
        try:
            async with db.transaction(pool, user) as conn:
                return await now_and_history.now(conn, now=moment)
        finally:
            await pool.close()

    return asyncio.run(read_view())


def entry_of(view: list[Any], item_id: str) -> Any:
    [found] = [entry for entry in view if str(entry.item.id) == item_id]
    return found


def replace(caller: Caller, old: str, new: str) -> Any:
    return caller.post(f"{ITEMS}/{old}/replace", {"replaced_by": new})


def view(caller: Caller, path: str) -> list[dict[str, Any]]:
    response = caller.get(path)
    assert response.status_code == 200, response.text
    found: list[dict[str, Any]] = response.json()
    return found


def test_a_state_entry_is_a_claim_about_now(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    assert alice.post(TOPICS, {"name": "Rust", "slug": "rust"}).status_code == 201

    written = write(
        alice, state(content="  Blocked on the reviewer.\n", topics=["rust"])
    )

    assert written["kind"] == "state"
    assert written["store"] == "semantic"
    assert written["nature"] == "claim"
    assert written["provenance"] == "asserted"
    assert written["status"] == "active"
    assert written["content"] == "Blocked on the reviewer."
    # A claim about now says nothing of when it happened.
    assert written["occurred_at"] is None and written["occurred_precision"] is None
    assert [t["slug"] for t in written["topics"]] == ["rust"]
    [evidence] = written["evidence"]
    assert evidence["kind"] == "supports"
    assert evidence["event"]["verb"] == "asserted"

    # Subjects are optional, as they are for a profile entry.
    named = write(alice, state(entities=["Personal platform"]))
    assert [n["name"] for n in named["entities"]] == ["Personal platform"]
    assert write(alice, state())["topics"] == []


def test_last_supported_is_the_newest_supporting_event(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    began = datetime.now(UTC).replace(microsecond=0) - timedelta(days=30)
    written = write(alice, state(occurred_at=iso(began)))

    [entry] = now_at(database, ALICE, datetime.now(UTC))
    # The asserted event, which happened when it was said to.
    assert entry.last_supported_at == began

    confirmed = act(alice, written["id"], "confirm")
    [event] = [
        e["event"] for e in confirmed["evidence"] if e["event"]["verb"] == "confirmed"
    ]

    [entry] = now_at(database, ALICE, datetime.now(UTC))
    assert entry.last_supported_at == when(event["occurred_at"]) > began

    # Computed when read, never stored: no column holds it, or the flag.
    with admin_connection(database) as conn:
        columns = {
            str(row[0])
            for row in conn.execute(
                "SELECT column_name FROM information_schema.columns"
                " WHERE table_name = 'items'"
            )
        }
    assert not {c for c in columns if "support" in c or "stale" in c}


def test_a_state_entry_is_stale_after_sixty_days_without_support(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    said = datetime(2026, 1, 1, 9, 0, tzinfo=UTC)
    written = write(alice, state(occurred_at=iso(said)))

    assert now_and_history.STALE_AFTER == timedelta(days=60)
    at_sixty = entry_of(
        now_at(database, ALICE, said + timedelta(days=60)), written["id"]
    )
    assert at_sixty.stale is False
    just_past = now_at(database, ALICE, said + timedelta(days=60, seconds=1))
    assert entry_of(just_past, written["id"]).stale is True
    at_sixty_one = now_at(database, ALICE, said + timedelta(days=61))
    assert entry_of(at_sixty_one, written["id"]).stale is True
    # Before it was said, nothing is stale either.
    assert entry_of(now_at(database, ALICE, said), written["id"]).stale is False


def test_confirming_records_an_event_and_clears_the_flag(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    written = write(
        alice, state(occurred_at=iso(datetime.now(UTC) - timedelta(days=90)))
    )
    assert entry_of(now_at(database, ALICE, datetime.now(UTC)), written["id"]).stale

    before = datetime.now(UTC)
    confirmed = act(alice, written["id"], "confirm")

    assert confirmed["id"] == written["id"]
    assert confirmed["status"] == "active"
    [support] = [e for e in confirmed["evidence"] if e["event"]["verb"] == "confirmed"]
    assert support["kind"] == "supports"
    assert support["event"]["origin"] == "brain"
    assert support["event"]["source"] == "manual-entry"
    assert before <= when(support["event"]["occurred_at"]) <= datetime.now(UTC)
    [event] = about(alice, written["id"], "confirmed")
    assert event["id"] == support["event"]["id"]
    assert event["subject_item"] == written["id"]
    assert support["event"]["evidence"] == (
        'Confirmed by hand: "Building the brain\'s memory store."'
    )

    entry = entry_of(now_at(database, ALICE, datetime.now(UTC)), written["id"])
    assert entry.stale is False

    # Allowed on any active claim, not only a state.
    assert act(alice, write(alice, profile())["id"], "confirm")["status"] == "active"
    assert act(alice, write(alice, rule())["id"], "confirm")["status"] == "active"

    # A record asserts nothing about now, so there is nothing to confirm.
    assert alice.post(TOPICS, {"name": "Rust", "slug": "rust"}).status_code == 201
    record = write(
        alice,
        {
            "kind": "history",
            "topic": "rust",
            "details": {"misconception": True, "believed": "x", "correct": "y"},
        },
    )
    assert record["nature"] == "record"
    retired = write(alice, state(content="Retired."))
    act(alice, retired["id"], "retire")
    before_counts = counts(database)
    response = alice.post(f"{ITEMS}/{record['id']}/confirm")
    assert response.status_code == 422, response.text
    # Only an active one: a retired item is restored first.
    response = alice.post(f"{ITEMS}/{retired['id']}/confirm")
    assert response.status_code == 409, response.text
    response = alice.post(f"{ITEMS}/{uuid.uuid7()}/confirm")
    assert response.status_code == 404, response.text
    response = calling(BOB).post(f"{ITEMS}/{written['id']}/confirm")
    assert response.status_code == 404, response.text
    assert counts(database) == before_counts


def test_a_history_entry_is_a_record_with_when_it_happened(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    assert alice.post(TOPICS, {"name": "Rust", "slug": "rust"}).status_code == 201
    before = datetime.now(UTC)

    written = write(
        alice,
        history(
            content="Finished the Rust book.",
            occurred_at="2023-05-01T00:00:00Z",
            occurred_precision="month",
            topics=["rust"],
            entities=["The Rust book"],
        ),
    )

    assert written["kind"] == "history"
    assert written["store"] == "reported_history"
    assert written["nature"] == "record"
    assert written["content"] == "Finished the Rust book."
    assert written["details"] == {}
    # When it happened, kept apart from when it was entered.
    assert written["occurred_at"] == "2023-05-01T00:00:00Z"
    assert written["occurred_precision"] == "month"
    assert before <= when(written["created_at"]) <= datetime.now(UTC)
    [evidence] = written["evidence"]
    assert evidence["event"]["verb"] == "asserted"
    assert evidence["event"]["occurred_at"] == "2023-05-01T00:00:00Z"
    assert evidence["event"]["occurred_precision"] == "month"
    assert before <= when(evidence["event"]["recorded_at"]) <= datetime.now(UTC)
    assert [t["slug"] for t in written["topics"]] == ["rust"]
    assert [n["name"] for n in written["entities"]] == ["The Rust book"]
    assert write(alice, history())["topics"] == []

    before_counts = counts(database)
    wrong: list[dict[str, Any]] = [
        # When it happened, and how precisely, are both required.
        history(occurred_at=...),
        history(occurred_precision=...),
        history(occurred_at="2023-05-01T00:00:00"),
        history(occurred_precision="week"),
        history(content="   "),
        history(content=...),
        history(topics=["haskell"]),
        history(category="goals"),
        history(value="solid"),
    ]
    for body in wrong:
        response = alice.post(ITEMS, body)
        assert response.status_code == 422, (body, response.text)
    assert counts(database) == before_counts


async def test_a_confirm_waits_for_a_retire_and_then_finds_it_retired(
    database: str,
) -> None:
    """The confirm takes the item's lock before it looks, so it never links
    support to an item retired while it was being confirmed."""
    pool = AsyncConnectionPool(app_url(database), min_size=2, max_size=2, open=False)
    await pool.open(wait=True)
    try:
        new = items.NewItem(
            kind="state",
            content="Writing tests.",
            category=None,
            topics=(),
            entities=(),
            occurred_at=None,
            occurred_precision="instant",
        )
        async with db.transaction(pool, ALICE) as conn:
            item_id = await items.write(conn, new, now=datetime.now(UTC))

        async def confirm() -> None:
            async with db.transaction(pool, ALICE) as conn:
                await items.confirm(conn, item_id, now=datetime.now(UTC))

        async with db.transaction(pool, ALICE) as conn:
            await items.retire(conn, item_id, now=datetime.now(UTC))
            second = asyncio.create_task(confirm())
            await asyncio.sleep(0.3)
            # Held by the retire, which has not committed.
            assert not second.done()
        with pytest.raises(Conflict):
            await second

        async with db.transaction(pool, ALICE) as conn:
            detail = await items.get(conn, item_id)
        assert detail is not None
        assert [e.event.verb for e in detail.evidence] == ["asserted", "retired"]
    finally:
        await pool.close()


def about_item(caller: Caller, item_id: str) -> list[dict[str, Any]]:
    """The events that made or judged this item: those naming it as their
    subject, and the one whose payload says it made it."""
    return [
        event
        for event in view(caller, "/brain/api/events")
        if event["subject_item"] == item_id or event["payload"].get("item") == item_id
    ]
