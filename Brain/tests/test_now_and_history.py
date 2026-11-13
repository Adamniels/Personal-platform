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
from tests.test_corrections import about, act, read
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
    assert act(alice, write(alice, decision())["id"], "confirm")["status"] == "active"

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


def test_a_state_entry_is_corrected_as_a_new_version(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    assert alice.post(TOPICS, {"name": "Rust", "slug": "rust"}).status_code == 201
    old = write(
        alice,
        state(
            content="Blocked on the reviewer.",
            topics=["rust"],
            entities=["Personal platform"],
            occurred_at=iso(datetime.now(UTC) - timedelta(days=90)),
        ),
    )
    assert entry_of(now_at(database, ALICE, datetime.now(UTC)), old["id"]).stale

    response = alice.post(
        f"{ITEMS}/{old['id']}/edit",
        {"kind": "state", "content": "Waiting on the reviewer."},
    )

    assert response.status_code == 201, response.text
    new = response.json()
    assert new["lineage_id"] == old["id"] and new["id"] != old["id"]
    assert (new["kind"], new["store"], new["nature"]) == ("state", "semantic", "claim")
    assert new["content"] == "Waiting on the reviewer."
    # Its subjects are carried over, as a profile entry's are.
    assert [topic["slug"] for topic in new["topics"]] == ["rust"]
    assert [entity["name"] for entity in new["entities"]] == ["Personal platform"]
    assert read(alice, old["id"])["inactive_reason"] == "superseded"
    # The owner has just said it, so it is current again, and the now view
    # holds the new version alone.
    current = now_at(database, ALICE, datetime.now(UTC))
    assert [str(entry.item.id) for entry in current] == [new["id"]]
    assert current[0].stale is False

    # Its subjects are changed the way a profile entry's are, and a state has
    # no category.
    emptied = alice.post(f"{ITEMS}/{new['id']}/edit", {"kind": "state", "entities": []})
    assert emptied.status_code == 201, emptied.text
    assert emptied.json()["entities"] == []
    refused = alice.post(
        f"{ITEMS}/{emptied.json()['id']}/edit",
        {"kind": "state", "category": "goals"},
    )
    assert refused.status_code == 422, refused.text


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


def test_a_decision_records_a_decided_event_and_a_standing_claim(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    assert alice.post(TOPICS, {"name": "Brain", "slug": "brain"}).status_code == 201
    before = datetime.now(UTC)

    written = write(alice, decision(topics=["brain"]))

    assert written["kind"] == "decision"
    assert written["store"] == "semantic"
    assert written["nature"] == "claim"
    assert written["status"] == "active"
    assert written["content"] == "The brain is its own service."
    assert written["details"] == {
        "reasoning": "Splitting memory across languages failed in v1.",
        "rejected": ["A memory module inside each feature"],
    }
    # When it was made: the request's time, unless told.
    made = when(written["occurred_at"])
    assert before <= made <= datetime.now(UTC)
    assert written["occurred_precision"] == "instant"
    [evidence] = written["evidence"]
    assert evidence["kind"] == "supports"
    assert evidence["event"]["verb"] == "decided"
    assert when(evidence["event"]["occurred_at"]) == made
    # Decided, not asserted: the event is the record that it was made.
    assert [e["verb"] for e in about_item(alice, written["id"])] == ["decided"]

    dated = write(
        alice,
        decision(
            occurred_at="2026-07-31T00:00:00Z",
            occurred_precision="day",
            details={"reasoning": "Hexagonal keeps the core testable."},
        ),
    )
    assert dated["occurred_at"] == "2026-07-31T00:00:00Z"
    assert dated["occurred_precision"] == "day"
    # Nothing was rejected.
    assert dated["details"] == {
        "reasoning": "Hexagonal keeps the core testable.",
        "rejected": [],
    }

    before_counts = counts(database)
    wrong: list[dict[str, Any]] = [
        decision(details=...),
        decision(details={"rejected": ["x"]}),
        decision(details={"reasoning": "   "}),
        decision(details={"reasoning": "Why.", "rejected": [""]}),
        decision(details={"reasoning": "Why.", "rejected": "x"}),
        decision(details={"reasoning": "Why.", "extra": 1}),
        decision(content="  "),
        decision(topics=["haskell"]),
        decision(category="goals"),
    ]
    for body in wrong:
        response = alice.post(ITEMS, body)
        assert response.status_code == 422, (body, response.text)
    assert counts(database) == before_counts


def about_item(caller: Caller, item_id: str) -> list[dict[str, Any]]:
    """The events that made or judged this item: those naming it as their
    subject, and the one whose payload says it made it."""
    return [
        event
        for event in view(caller, "/brain/api/events")
        if event["subject_item"] == item_id or event["payload"].get("item") == item_id
    ]


def test_a_decision_can_be_replaced_by_another(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    old = write(alice, decision(content="Use hexagonal architecture."))
    new = write(alice, decision(content="Use a plain layered architecture."))

    response = replace(alice, old["id"], new["id"])

    assert response.status_code == 200, response.text
    replaced = response.json()
    assert replaced["id"] == old["id"]
    assert replaced["status"] == "inactive"
    assert replaced["inactive_reason"] == "superseded"
    # The new one replaced it, and holds.
    [relation] = replaced["relations"]
    assert relation["kind"] == "supersedes"
    assert relation["from_item"] == new["id"] and relation["to_item"] == old["id"]
    assert relation["ended_at"] is None
    # A judgment against the old one, which made the relation.
    [judged] = [e for e in replaced["evidence"] if e["event"]["verb"] == "edited"]
    assert judged["kind"] == "contradicts"
    assert relation["created_by_event"] == judged["event"]["id"]
    assert judged["event"]["evidence"] == (
        'Replaced by hand: "Use hexagonal architecture."'
        ' by "Use a plain layered architecture."'
    )

    # Both stay readable, and the new one shows the relation too.
    after = read(alice, new["id"])
    assert after["status"] == "active"
    assert [r["id"] for r in after["relations"]] == [relation["id"]]
    assert read(alice, old["id"])["status"] == "inactive"
    # The decided event that recorded the old one is untouched; newest first.
    assert [e["verb"] for e in about_item(alice, old["id"])] == ["edited", "decided"]

    # The old one shows what replaced it.
    entries = {entry["id"]: entry for entry in view(alice, HISTORY)}
    assert entries[old["id"]]["replaced_by"] == new["id"]
    assert entries[new["id"]]["replaced_by"] is None

    # Replaced once: neither it nor what replaced it can now be replaced by it.
    third = write(alice, decision(content="Use something else."))
    before_counts = counts(database)
    assert replace(alice, old["id"], third["id"]).status_code == 409
    assert replace(alice, third["id"], old["id"]).status_code == 409
    assert replace(alice, new["id"], old["id"]).status_code == 409
    assert counts(database) == before_counts

    # Another user's is not there.
    bob = calling(BOB)
    bobs = write(bob, decision())
    assert replace(bob, bobs["id"], third["id"]).status_code == 422
    assert replace(bob, third["id"], bobs["id"]).status_code == 404


def test_replacing_needs_two_decisions(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    one = write(alice, decision())
    others = [
        write(alice, state()),
        write(alice, history()),
        write(alice, profile()),
        write(alice, rule()),
    ]
    before_counts = counts(database)

    for other in others:
        response = replace(alice, one["id"], other["id"])
        assert response.status_code == 422, (other["kind"], response.text)
        response = replace(alice, other["id"], one["id"])
        assert response.status_code == 422, (other["kind"], response.text)

    # The newer one is one the user has.
    assert replace(alice, one["id"], str(uuid.uuid7())).status_code == 422
    assert replace(alice, str(uuid.uuid7()), one["id"]).status_code == 404
    for body in [
        None,
        {},
        {"replaced_by": "not an id"},
        {"replaced_by": one["id"], "x": 1},
    ]:
        response = alice.post(f"{ITEMS}/{one['id']}/replace", body)
        assert response.status_code == 422, (body, response.text)
    assert counts(database) == before_counts


def test_a_decision_cannot_replace_itself_or_a_version_of_itself(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    first = write(alice, decision())
    first_id = uuid.UUID(first["id"])

    async def edit_it() -> uuid.UUID:
        pool = AsyncConnectionPool(
            app_url(database), min_size=1, max_size=1, open=False
        )
        await pool.open(wait=True)
        try:
            async with db.transaction(pool, ALICE) as conn:
                return await items.edit(
                    conn,
                    first_id,
                    items.Edit(
                        kind="decision",
                        content="The brain is its own service, in Python.",
                        category=None,
                        topics=None,
                        entities=None,
                    ),
                    now=datetime.now(UTC),
                )
        finally:
            await pool.close()

    # A decision has no edit route yet; its versions are made the way one is.
    version = str(asyncio.run(edit_it()))
    before_counts = counts(database)

    response = replace(alice, version, version)
    assert response.status_code == 422, response.text
    # Of its own lineage, whichever way round, before whether either is active.
    response = replace(alice, version, first["id"])
    assert response.status_code == 422, response.text
    response = replace(alice, first["id"], version)
    assert response.status_code == 422, response.text
    assert counts(database) == before_counts


async def test_two_decisions_replacing_each_other_at_once_replace_once(
    database: str,
) -> None:
    """Each locks both before it looks, so the second waits for the first and
    then finds its decision inactive, rather than each superseding the
    other."""
    pool = AsyncConnectionPool(app_url(database), min_size=2, max_size=2, open=False)
    await pool.open(wait=True)
    try:

        def made(content: str) -> items.NewItem:
            return items.NewItem(
                kind="decision",
                content=content,
                category=None,
                topics=(),
                entities=(),
                occurred_at=None,
                occurred_precision="instant",
                details={"reasoning": "Why.", "rejected": []},
            )

        async with db.transaction(pool, ALICE) as conn:
            first = await items.write(conn, made("First."), now=datetime.now(UTC))
            second = await items.write(conn, made("Second."), now=datetime.now(UTC))

        async def the_other_way() -> None:
            async with db.transaction(pool, ALICE) as conn:
                await items.replace_decision(conn, second, first, now=datetime.now(UTC))

        async with db.transaction(pool, ALICE) as conn:
            await items.replace_decision(conn, first, second, now=datetime.now(UTC))
            other = asyncio.create_task(the_other_way())
            await asyncio.sleep(0.3)
            # Held by the first, which has not committed.
            assert not other.done()
        with pytest.raises(Conflict):
            await other

        async with db.transaction(pool, ALICE) as conn:
            found = await items.listing(
                conn, kind="decision", status=None, topic=None, entity=None
            )
        assert {(i.id, i.status) for i in found} == {
            (first, "inactive"),
            (second, "active"),
        }
    finally:
        await pool.close()


def test_a_record_is_never_stale(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    ten_years_ago = datetime.now(UTC).replace(microsecond=0) - timedelta(days=3653)
    record = write(
        alice, history(occurred_at=iso(ten_years_ago), occurred_precision="day")
    )
    claim = write(alice, state(occurred_at=iso(ten_years_ago)))

    # Only a state is read for currency; a record has none.
    now_view = now_at(database, ALICE, datetime.now(UTC))
    assert [str(entry.item.id) for entry in now_view] == [claim["id"]]
    assert now_view[0].stale is True

    # The history view carries no flag at all.
    [entry] = view(alice, HISTORY)
    assert entry["id"] == record["id"]
    assert "stale" not in entry and "last_supported_at" not in entry


def test_history_is_ordered_by_when_it_happened(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    assert alice.post(TOPICS, {"name": "Rust", "slug": "rust"}).status_code == 201
    week_ago = datetime.now(UTC).replace(microsecond=0) - timedelta(days=7)
    decided = write(
        alice, decision(occurred_at=iso(week_ago), topics=["rust"], entities=["Brain"])
    )
    # Entered after the decision, but it happened long before it.
    course = write(
        alice,
        history(
            content="Took a Rust course.",
            occurred_at="2023-03-01T00:00:00Z",
            occurred_precision="month",
            topics=["rust"],
        ),
    )

    entries = view(alice, HISTORY)

    assert [e["id"] for e in entries] == [course["id"], decided["id"]]
    [course_entry, decided_entry] = entries
    assert course_entry["kind"] == "history"
    assert course_entry["occurred_at"] == "2023-03-01T00:00:00Z"
    assert course_entry["replaced_by"] is None
    assert decided_entry["details"]["reasoning"]

    # What is not history, and what was put away, is left out.
    write(alice, state(topics=["rust"]))
    write(alice, profile())
    retired = write(
        alice, history(content="Retired.", occurred_at="2020-01-01T00:00:00Z")
    )
    act(alice, retired["id"], "retire")
    assert [e["id"] for e in view(alice, HISTORY)] == [course["id"], decided["id"]]

    # Filtered by topic and by named thing.
    write(alice, history(content="Elsewhere.", occurred_at="2024-01-01T00:00:00Z"))
    assert [e["id"] for e in view(alice, f"{HISTORY}?topic=rust")] == [
        course["id"],
        decided["id"],
    ]
    assert [e["id"] for e in view(alice, f"{HISTORY}?entity=brain")] == [decided["id"]]
    assert view(alice, f"{HISTORY}?topic=haskell") == []
    assert alice.get(f"{HISTORY}?topic=Not%20A%20Slug").status_code == 422

    # Each user's own.
    assert view(calling(BOB), HISTORY) == []


def test_the_now_view_lists_active_states_newest_support_first(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    now = datetime.now(UTC).replace(microsecond=0)
    old = write(alice, state(content="Old.", occurred_at=iso(now - timedelta(days=90))))
    recent = write(
        alice, state(content="Recent.", occurred_at=iso(now - timedelta(days=2)))
    )
    middle = write(
        alice, state(content="Middle.", occurred_at=iso(now - timedelta(days=30)))
    )
    retired = write(alice, state(content="Retired."))
    act(alice, retired["id"], "retire")
    write(alice, decision())
    write(alice, history())
    write(alice, profile())

    entries = view(alice, NOW)

    assert [e["id"] for e in entries] == [recent["id"], middle["id"], old["id"]]
    assert [e["stale"] for e in entries] == [False, False, True]
    assert [when(e["last_supported_at"]) for e in entries] == [
        now - timedelta(days=2),
        now - timedelta(days=30),
        now - timedelta(days=90),
    ]
    assert entries[0]["kind"] == "state" and entries[0]["content"] == "Recent."

    # Confirming the oldest brings it to the front, and clears its flag.
    act(alice, old["id"], "confirm")
    entries = view(alice, NOW)
    assert [e["id"] for e in entries] == [old["id"], recent["id"], middle["id"]]
    assert entries[0]["stale"] is False

    assert view(calling(BOB), NOW) == []
