"""Events, what happened, recorded through the API and read back as the
timeline, on a database of the test's own, as brain_app. The shape is "The
shape" in `Brain/docs/event-model.md`, the table and the verb list are in
`Brain/docs/data-model.md`."""

import re
import uuid
from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

import psycopg
import pytest
from psycopg_pool import AsyncConnectionPool

from brain import db
from tests.conftest import Caller
from tests.harness import admin_connection

ALICE = uuid.UUID("0190a8f0-0000-7000-8000-00000000a11c")
BOB = uuid.UUID("0190a8f0-0000-7000-8000-000000000b0b")

EVENTS = "/brain/api/events"
TOPICS = "/brain/api/topics"
ENTITIES = "/brain/api/entities"

DATA_MODEL = Path(__file__).resolve().parents[1] / "docs" / "data-model.md"


def event(**overrides: Any) -> dict[str, Any]:
    """A well formed event, the first example in "The shape", with a key of
    its own unless one is given."""
    body: dict[str, Any] = {
        "verb": "exposed",
        "topic": "postgres-indexing",
        "origin": "news",
        "run": "r-8812",
        "evidence": (
            "Shown the article 'Index only scans and when they stop working',"
            " ranked third, surfaced for backend architecture interest."
        ),
        "payload": {"rank": 3, "score": 0.82},
        "payload_version": 1,
        "idempotency_key": str(uuid.uuid4()),
    }
    body.update(overrides)
    return {key: value for key, value in body.items() if value is not ...}


def record(caller: Caller, body: dict[str, Any]) -> dict[str, Any]:
    response = caller.post(EVENTS, body)
    assert response.status_code == 201, response.text
    recorded: dict[str, Any] = response.json()
    return recorded


def timeline(caller: Caller, query: str = "") -> list[dict[str, Any]]:
    response = caller.get(f"{EVENTS}{query}")
    assert response.status_code == 200, response.text
    events: list[dict[str, Any]] = response.json()
    return events


def rows(database: str) -> list[tuple[Any, ...]]:
    """Every event as stored, read as the superuser, past row level security."""
    with admin_connection(database) as conn:
        return conn.execute(
            "SELECT id, user_id, subject_topic, subject_entity, origin,"
            " idempotency_key FROM events ORDER BY id"
        ).fetchall()


def test_an_event_is_recorded_with_every_part_of_the_shape(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    before = datetime.now(UTC)
    body = event(
        verb="abandoned",
        source="session",
        occurred_at="2026-07-31T07:02:00+00:00",
        payload={"sections_done": 2, "sections_total": 6, "stopped_at": "page splits"},
        payload_version=2,
    )

    recorded = record(alice, body)

    assert uuid.UUID(recorded["id"]).version == 7
    assert recorded["verb"] == "abandoned"
    assert recorded["topic"]["slug"] == "postgres-indexing"
    assert recorded["subject_topic"] == recorded["topic"]["id"]
    assert recorded["entity"] is None
    assert recorded["subject_item"] is None
    assert recorded["origin"] == "news"
    assert recorded["source"] == "session"
    assert recorded["run"] == "r-8812"
    assert datetime.fromisoformat(recorded["occurred_at"]) == datetime(
        2026, 7, 31, 7, 2, tzinfo=UTC
    )
    assert recorded["occurred_precision"] == "instant"
    recorded_at = datetime.fromisoformat(recorded["recorded_at"])
    assert before <= recorded_at <= datetime.now(UTC)
    assert recorded["evidence"] == body["evidence"]
    assert recorded["payload"] == body["payload"]
    assert recorded["payload_version"] == 2
    assert recorded["idempotency_key"] == body["idempotency_key"]
    assert timeline(alice) == [recorded]


def test_an_event_about_a_named_thing_or_nothing_is_recorded(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    project = alice.post(ENTITIES, {"name": "Personal Platform", "label": "project"})
    assert project.status_code == 201

    about = record(alice, event(verb="decided", topic=..., entity="personal platform"))
    unscoped = record(alice, event(verb="worked", topic=...))

    assert about["entity"] == {"id": project.json()["id"], "name": "Personal Platform"}
    assert about["topic"] is None
    assert unscoped["topic"] is None and unscoped["entity"] is None
    # An unknown named thing is created, with no label.
    other = record(alice, event(topic=..., entity="Operation Rollout"))
    names = {e["name"]: e for e in alice.get(ENTITIES).json()}
    assert names["Operation Rollout"]["label"] is None
    assert other["entity"]["id"] == names["Operation Rollout"]["id"]


@pytest.mark.parametrize(
    "evidence",
    [..., "", "   \n\t "],
    ids=["missing", "empty", "whitespace only"],
)
def test_an_event_without_readable_evidence_is_refused(
    evidence: Any, calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)

    assert alice.post(EVENTS, event(evidence=evidence)).status_code == 422
    assert rows(database) == []


def test_an_unknown_verb_is_refused(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)

    response = alice.post(EVENTS, event(verb="glanced"))

    assert response.status_code == 422
    assert "glanced" in response.json()["detail"]
    assert rows(database) == []
    # Nor did the refused event leave a topic behind.
    assert alice.get(TOPICS).json() == []


@pytest.mark.parametrize(
    "overrides",
    [
        {"topic": "Postgres Indexing"},
        {"topic": "-leading-hyphen"},
        {"topic": "a" * 81},
        {"topic": "postgres", "entity": "Personal Platform"},
        {"topic": ..., "entity": "   "},
        {"origin": "brain"},
        {"origin": "News"},
        {"origin": ""},
        {"origin": ...},
        {"occurred_at": "2026-07-31T07:02:00"},
        {"occurred_precision": "week"},
        {"payload": [1, 2]},
        {"payload": "text"},
        {"payload": {"blob": "x" * (16 * 1024)}},
        {"payload": {"text": "nul \u0000 inside"}},
        {"evidence": "nul \u0000 inside"},
        {"payload_version": 0},
        {"payload_version": ...},
        {"idempotency_key": ""},
        {"idempotency_key": ...},
        {"item": str(uuid.uuid7())},
        {"source": ""},
        {"run": ""},
    ],
    ids=[
        "slug not lowercase",
        "slug leading hyphen",
        "slug too long",
        "topic and entity",
        "blank entity",
        "origin brain",
        "origin not lowercase",
        "origin empty",
        "origin missing",
        "occurred without a zone",
        "unknown precision",
        "payload a list",
        "payload text",
        "payload over 16 KiB",
        "payload with nul",
        "evidence with nul",
        "payload version zero",
        "payload version missing",
        "key empty",
        "key missing",
        "item not there",
        "source empty",
        "run empty",
    ],
)
def test_a_malformed_event_is_refused(
    overrides: dict[str, Any],
    calling: Callable[[uuid.UUID], Caller],
    database: str,
) -> None:
    alice = calling(ALICE)

    response = alice.post(EVENTS, event(**overrides))

    assert response.status_code == 422, response.text
    assert rows(database) == []
    assert alice.get(TOPICS).json() == []


def test_a_payload_of_exactly_16_kib_is_recorded(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    # {"blob":"..."} compact is 11 bytes around the text.
    payload = {"blob": "x" * (16 * 1024 - 11)}

    assert record(alice, event(payload=payload))["payload"] == payload


def test_the_same_event_sent_twice_is_recorded_once(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    body = event()
    first = record(alice, body)

    again = alice.post(EVENTS, body)
    # A retry may differ in what it carries; the key alone says it is the
    # same event, and the first one stands.
    changed = alice.post(EVENTS, {**body, "evidence": "Sent again, reworded."})

    assert again.status_code == 200, again.text
    assert again.json() == first
    assert changed.status_code == 200, changed.text
    assert changed.json() == first
    assert len(rows(database)) == 1


def test_the_same_key_from_another_origin_is_a_different_event(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    key = str(uuid.uuid4())

    news = record(alice, event(origin="news", idempotency_key=key))
    learning = record(alice, event(origin="learning", idempotency_key=key))

    assert news["id"] != learning["id"]
    assert {row[4] for row in rows(database)} == {"news", "learning"}


def test_an_event_about_an_unknown_topic_creates_it_as_provisional(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)

    recorded = record(alice, event(topic="b-tree-internals"))
    record(alice, event(topic="b-tree-internals"))

    [topic] = alice.get(TOPICS).json()
    assert topic["slug"] == "b-tree-internals"
    assert topic["state"] == "provisional"
    assert topic["alias_of"] is None
    assert recorded["topic"] == {
        "id": topic["id"],
        "slug": "b-tree-internals",
        "name": topic["name"],
    }


def test_an_event_about_an_alias_is_stored_on_the_alias_and_read_on_its_target(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    target = alice.post(TOPICS, {"name": "Postgres indexing"}).json()
    alias = alice.post(TOPICS, {"name": "Database indexes"}).json()
    same = alice.post(f"{TOPICS}/{alias['id']}/same-as", {"target_id": target["id"]})
    assert same.status_code == 200

    recorded = record(alice, event(topic="database-indexes"))

    [row] = rows(database)
    assert row[2] == uuid.UUID(alias["id"])
    assert recorded["subject_topic"] == alias["id"]
    assert recorded["topic"]["id"] == target["id"]
    assert recorded["topic"]["slug"] == "postgres-indexing"
    # Separated again, the same event is read on the alias, which it named.
    alice.post(f"{TOPICS}/{alias['id']}/separate")
    [read] = timeline(alice)
    assert read["topic"]["id"] == alias["id"]


def test_occurred_and_recorded_are_kept_apart(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    three_years_ago = datetime.now(UTC) - timedelta(days=3 * 365)

    recorded = record(
        alice,
        event(
            verb="asserted",
            topic="distributed-systems",
            origin="learning",
            source="manual-entry",
            occurred_at=three_years_ago.isoformat(),
            occurred_precision="year",
        ),
    )
    [read] = timeline(alice)

    for found in (recorded, read):
        assert datetime.fromisoformat(found["occurred_at"]) == three_years_ago
        assert found["occurred_precision"] == "year"
        recorded_at = datetime.fromisoformat(found["recorded_at"])
        assert recorded_at - three_years_ago > timedelta(days=3 * 365 - 1)


def test_an_event_without_occurred_happened_when_it_was_recorded(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)

    recorded = record(alice, event(occurred_at=...))

    assert recorded["occurred_at"] == recorded["recorded_at"]
    assert recorded["occurred_precision"] == "instant"


UPDATES = [
    "UPDATE events SET evidence = 'rewritten'",
    "UPDATE events SET payload = '{}'",
    "UPDATE events SET recorded_at = now()",
    "DELETE FROM events",
    "TRUNCATE events",
    "INSERT INTO verbs VALUES ('glanced', 'engagement', 1, 1)",
    "UPDATE verbs SET strength = 1",
    "DELETE FROM verbs",
]


@pytest.mark.parametrize("statement", UPDATES)
async def test_the_service_cannot_change_or_remove_an_event(
    statement: str, pool: AsyncConnectionPool, database: str
) -> None:
    # On a stored row, so a refusal is the role's and not an empty table's.
    with admin_connection(database) as conn:
        conn.execute(
            "INSERT INTO events (id, user_id, verb, subject_topic, subject_entity,"
            " subject_item, origin, source, run, occurred_at, occurred_precision,"
            " recorded_at, evidence, payload, payload_version, idempotency_key)"
            " VALUES (%s, %s, 'opened', NULL, NULL, NULL, 'news', NULL, NULL,"
            " now(), 'instant', now(), 'Opened it.', '{}', 1, 'k')",
            (uuid.uuid7(), ALICE),
        )

    with pytest.raises(psycopg.errors.InsufficientPrivilege, match="permission denied"):
        async with db.transaction(pool, ALICE) as conn:
            await conn.execute(statement)


async def test_the_service_reads_events_and_verbs_and_adds_events(
    pool: AsyncConnectionPool,
) -> None:
    # The control for the test above.
    async with db.transaction(pool, ALICE) as conn:
        await conn.execute("SELECT * FROM verbs")
        await conn.execute("SELECT * FROM events")
        await conn.execute(
            "INSERT INTO events (id, user_id, verb, subject_topic, subject_entity,"
            " subject_item, origin, source, run, occurred_at, occurred_precision,"
            " recorded_at, evidence, payload, payload_version, idempotency_key)"
            f" VALUES (%s, {db.CURRENT_USER_SQL}, 'opened', NULL, NULL, NULL,"
            " 'news', NULL, NULL, now(), 'instant', now(), 'Opened it.', '{}',"
            " 1, 'k')",
            (uuid.uuid7(),),
        )


def test_the_timeline_is_newest_first_and_paged(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    recorded = [record(alice, event(evidence=f"Event {n}.")) for n in range(5)]
    newest_first = list(reversed(recorded))

    assert timeline(alice) == newest_first
    first = timeline(alice, "?limit=2")
    assert first == newest_first[:2]
    second = timeline(alice, f"?limit=2&before={_q(first[-1]['recorded_at'])}")
    assert second == newest_first[2:4]
    third = timeline(alice, f"?limit=2&before={_q(second[-1]['recorded_at'])}")
    assert third == newest_first[4:]
    assert timeline(alice, f"?before={_q(third[-1]['recorded_at'])}") == []


def _q(value: str) -> str:
    # A recorded time in a query string: its + must not read as a space.
    return value.replace("+", "%2B")


def test_the_timeline_answers_50_by_default_and_200_at_most(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    # Written as the superuser, since 201 requests would only be slow.
    start = datetime(2026, 1, 1, tzinfo=UTC)
    with admin_connection(database) as conn, conn.cursor() as cursor:
        cursor.executemany(
            "INSERT INTO events (id, user_id, verb, subject_topic, subject_entity,"
            " subject_item, origin, source, run, occurred_at, occurred_precision,"
            " recorded_at, evidence, payload, payload_version, idempotency_key)"
            " VALUES (%s, %s, 'opened', NULL, NULL, NULL, 'news', NULL, NULL,"
            " %s, 'instant', %s, 'Opened it.', '{}', 1, %s)",
            [
                (
                    uuid.uuid7(),
                    ALICE,
                    start + timedelta(minutes=n),
                    start + timedelta(minutes=n),
                    f"k{n}",
                )
                for n in range(201)
            ],
        )

    assert len(timeline(alice)) == 50
    assert len(timeline(alice, "?limit=200")) == 200
    for query in [
        "?limit=0",
        "?limit=201",
        "?limit=many",
        "?before=yesterday",
        "?before=2026-01-01T00:00:00",
    ]:
        assert alice.get(f"{EVENTS}{query}").status_code == 422, query


def test_the_timeline_pages_past_events_recorded_at_the_same_time(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    """Correcting an item records two events in one transaction, at one
    time, so a page can end between them: the next one starts after the
    last one's id as well as its time, and loses neither."""
    alice = calling(ALICE)
    at = datetime(2026, 11, 12, 9, tzinfo=UTC)
    ids = sorted(uuid.uuid7() for _ in range(3))
    with admin_connection(database) as conn, conn.cursor() as cursor:
        cursor.executemany(
            "INSERT INTO events (id, user_id, verb, subject_topic, subject_entity,"
            " subject_item, origin, source, run, occurred_at, occurred_precision,"
            " recorded_at, evidence, payload, payload_version, idempotency_key)"
            " VALUES (%s, %s, 'opened', NULL, NULL, NULL, 'news', NULL, NULL,"
            " %s, 'instant', %s, 'Opened it.', '{}', 1, %s)",
            [(id_, ALICE, at, at, f"k{n}") for n, id_ in enumerate(ids)],
        )

    first = timeline(alice, "?limit=2")
    # Newest first, the id breaking the tie, as the index orders them.
    assert [e["id"] for e in first] == [str(ids[2]), str(ids[1])]
    last = first[-1]
    rest = timeline(
        alice, f"?limit=2&before={_q(last['recorded_at'])}&before_id={last['id']}"
    )
    assert [e["id"] for e in rest] == [str(ids[0])]
    # The time alone skips what shares it, which before_id is for.
    assert timeline(alice, f"?before={_q(last['recorded_at'])}") == []
    for query in [
        f"?before_id={last['id']}",
        f"?before={_q(last['recorded_at'])}&before_id=not-an-id",
    ]:
        assert alice.get(f"{EVENTS}{query}").status_code == 422, query


def test_an_event_names_the_item_it_made(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    """The event that makes an item cannot name it as its subject, since it
    comes first; the timeline says which item it made, so it can link to
    it. A judgment names its item as its subject and made none."""
    alice = calling(ALICE)
    made = alice.post(
        "/brain/api/items",
        {"kind": "profile", "category": "values", "content": "Lives in Uppsala."},
    )
    assert made.status_code == 201, made.text
    old = made.json()["id"]
    edited = alice.post(
        f"/brain/api/items/{old}/edit",
        {"kind": "profile", "content": "Lives in Stockholm."},
    )
    assert edited.status_code == 201, edited.text
    new = edited.json()["id"]
    sent = record(alice, event())

    by_verb = {
        (e["verb"], e["origin"]): (e["subject_item"], e["made_item"])
        for e in timeline(alice)
    }
    assert len(timeline(alice)) == 4
    assert by_verb[("edited", "brain")] == (old, None)
    assert by_verb[("exposed", "news")] == (None, None)
    entered = [e["made_item"] for e in timeline(alice) if e["verb"] == "asserted"]
    assert entered == [new, old]
    assert sent["made_item"] is None


def test_events_are_scoped_to_their_user(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice, bob = calling(ALICE), calling(BOB)
    body = event()
    hers = record(alice, body)

    assert timeline(bob) == []
    # Her key is hers: the same one from Bob is his own event.
    his = record(bob, body)
    assert his["id"] != hers["id"]
    # On his own topic of the same slug, made for him.
    assert his["topic"]["id"] != hers["topic"]["id"]
    assert [t["slug"] for t in bob.get(TOPICS).json()] == ["postgres-indexing"]
    assert timeline(alice) == [hers]
    assert timeline(bob) == [his]
    assert {row[1] for row in rows(database)} == {ALICE, BOB}


def documented_verbs() -> set[tuple[str, str, int, int]]:
    """The rows of the table under "The verb list" in the data model doc."""
    text = DATA_MODEL.read_text()
    section = text.split("## The verb list", 1)[1].split("\n## ", 1)[0]
    found = set()
    for line in section.splitlines():
        match = re.match(
            r"^\|\s*`(\w+)`\s*\|\s*(\w+)\s*\|\s*(\d)\s*\|\s*(-?\d)\s*\|$", line
        )
        if match:
            verb, kind, strength, polarity = match.groups()
            found.add((verb, kind, int(strength), int(polarity)))
    return found


def test_every_verb_declares_strength_and_polarity(database: str) -> None:
    with admin_connection(database) as conn:
        seeded = {
            (str(verb), str(kind), int(str(strength)), int(str(polarity)))
            for verb, kind, strength, polarity in conn.execute(
                "SELECT verb, kind, strength, polarity FROM verbs"
            )
        }

    documented = documented_verbs()
    assert len(documented) == 17
    assert seeded == documented
