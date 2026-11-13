"""Broader and narrower: one topic placed under another, and ended again,
never deleted. Through the API, on a database of the test's own, as
brain_app. The rules are in "Relations" in `Brain/docs/event-model.md`."""

import uuid
from collections.abc import Callable
from datetime import UTC, datetime
from typing import Any

from psycopg_pool import AsyncConnectionPool

from brain import db, topics
from tests.conftest import Caller
from tests.harness import admin_connection

ALICE = uuid.UUID("0190a8f0-0000-7000-8000-00000000a11c")
BOB = uuid.UUID("0190a8f0-0000-7000-8000-000000000b0b")

TOPICS = "/brain/api/topics"


def create(caller: Caller, name: str) -> dict[str, Any]:
    response = caller.post(TOPICS, {"name": name})
    assert response.status_code == 201, response.text
    topic: dict[str, Any] = response.json()
    return topic


def under(caller: Caller, narrower: dict[str, Any], broader: dict[str, Any]) -> Any:
    """Places `narrower` under `broader`."""
    return caller.post(
        f"{TOPICS}/{narrower['id']}/broader", {"broader_id": broader["id"]}
    )


def end(caller: Caller, relation: dict[str, Any]) -> Any:
    return caller.post(f"/brain/api/topic-relations/{relation['id']}/end")


def test_broader_than_can_be_set_and_ended(
    database: str, calling: Callable[[uuid.UUID], Caller]
) -> None:
    alice = calling(ALICE)
    postgres = create(alice, "Postgres")
    databases = create(alice, "Databases")

    response = under(alice, postgres, databases)

    assert response.status_code == 201, response.text
    relation = response.json()
    assert relation["from_topic"] == databases["id"]
    assert relation["to_topic"] == postgres["id"]
    assert relation["kind"] == "broader_than"
    assert relation["ended_at"] is None

    ended = end(alice, relation)

    assert ended.status_code == 200, ended.text
    assert ended.json()["ended_at"] is not None
    assert {k: v for k, v in ended.json().items() if k != "ended_at"} == {
        k: v for k, v in relation.items() if k != "ended_at"
    }
    # The row stays, ended.
    with admin_connection(database) as conn:
        rows = conn.execute(
            "SELECT id, ended_at IS NOT NULL FROM topic_relations"
        ).fetchall()
    assert rows == [(uuid.UUID(relation["id"]), True)]
    # Ending it again leaves the time it ended.
    again = end(alice, relation)
    assert again.status_code == 200
    assert again.json() == ended.json()


def test_a_relation_that_holds_is_not_added_twice(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    postgres = create(alice, "Postgres")
    databases = create(alice, "Databases")
    first = under(alice, postgres, databases).json()

    assert under(alice, postgres, databases).status_code == 409

    # Once it has ended, it may be set again, as a new row.
    assert end(alice, first).status_code == 200
    second = under(alice, postgres, databases)
    assert second.status_code == 201
    assert second.json()["id"] != first["id"]


def test_broader_than_refuses_a_cycle(calling: Callable[[uuid.UUID], Caller]) -> None:
    alice = calling(ALICE)
    a = create(alice, "A")
    b = create(alice, "B")
    c = create(alice, "C")

    # Under itself.
    assert under(alice, a, a).status_code == 422
    a_under_b = under(alice, a, b)
    assert a_under_b.status_code == 201
    # Directly.
    assert under(alice, b, a).status_code == 422
    assert under(alice, b, c).status_code == 201
    # Through a third: A under B under C, so C under A closes the loop.
    assert under(alice, c, a).status_code == 422

    # Once A is no longer under B, nothing closes, and B may go under A.
    assert end(alice, a_under_b.json()).status_code == 200
    assert under(alice, b, a).status_code == 201


def test_a_cycle_through_an_alias_is_refused(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    a = create(alice, "A")
    b = create(alice, "B")
    alias = create(alice, "Old A")
    assert under(alice, a, b).status_code == 201
    assert (
        alice.post(
            f"{TOPICS}/{alias['id']}/same-as", {"target_id": a["id"]}
        ).status_code
        == 200
    )

    # The alias is read as A, and A is already under B.
    assert under(alice, b, alias).status_code == 422
    assert under(alice, alias, a).status_code == 422


def test_making_two_related_topics_the_same_is_refused(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    # A under B, then A the same as B, would put B under itself.
    alice = calling(ALICE)
    a = create(alice, "A")
    b = create(alice, "B")
    c = create(alice, "C")
    assert under(alice, a, b).status_code == 201
    assert under(alice, b, c).status_code == 201

    for topic, target in [(a, b), (b, a), (a, c), (c, a)]:
        response = alice.post(
            f"{TOPICS}/{topic['id']}/same-as", {"target_id": target["id"]}
        )
        assert response.status_code == 422, (topic["slug"], target["slug"])


def test_the_broader_topic_must_exist(calling: Callable[[uuid.UUID], Caller]) -> None:
    alice = calling(ALICE)
    a = create(alice, "A")

    assert under(alice, a, {"id": str(uuid.uuid7())}).status_code == 422


async def test_resolve_and_narrower_read_through_aliases(
    pool: AsyncConnectionPool,
) -> None:
    now = datetime.now(UTC)
    async with db.transaction(pool, ALICE) as conn:
        databases = await topics.create(conn, "Databases", "databases", now=now)
        postgres = await topics.create(conn, "Postgres", "postgres", now=now)
        indexes = await topics.create(conn, "Indexes", "indexes", now=now)
        btree = await topics.create(conn, "B-tree", "b-tree", now=now)
        old = await topics.create(
            conn, "Postgres indexing", "postgres-indexing", now=now
        )
        ended = await topics.create(conn, "MySQL", "mysql", now=now)
        await topics.add_broader(conn, postgres.id, databases.id, now=now)
        # Attached to the alias, read through it as the target.
        await topics.add_broader(conn, old.id, postgres.id, now=now)
        await topics.add_broader(conn, btree.id, old.id, now=now)
        gone = await topics.add_broader(conn, ended.id, databases.id, now=now)
        await topics.end_relation(conn, gone.id, now=now)
        await topics.same_as(conn, old.id, indexes.id)

        assert await topics.resolve(conn, old.id) == indexes.id
        assert await topics.resolve(conn, indexes.id) == indexes.id
        assert await topics.resolve(conn, uuid.uuid7()) is None
        assert await topics.narrower(conn, databases.id) == {
            postgres.id,
            indexes.id,
            btree.id,
        }
        # Asked about the alias, answered about its target.
        assert await topics.narrower(conn, old.id) == {btree.id}
        assert await topics.narrower(conn, btree.id) == set()

    async with db.transaction(pool, BOB) as conn:
        assert await topics.narrower(conn, databases.id) == set()
        assert await topics.resolve(conn, old.id) is None
