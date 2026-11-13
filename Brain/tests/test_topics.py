"""Topics, the brain's subjects: created canonical, made the same as another
and separated again, and promoted from provisional. Through the API, on a
database of the test's own, as brain_app. The rules are in "Topic
vocabulary" in `Brain/docs/event-model.md` and the table in
`Brain/docs/data-model.md`."""

import asyncio
import uuid
from collections.abc import Callable
from datetime import UTC, datetime
from typing import Any

import psycopg
import pytest
from fastapi.testclient import TestClient
from psycopg_pool import AsyncConnectionPool

from brain import db, topics
from tests.conftest import Caller
from tests.harness import admin_connection, app_url

ALICE = uuid.UUID("0190a8f0-0000-7000-8000-00000000a11c")
BOB = uuid.UUID("0190a8f0-0000-7000-8000-000000000b0b")

TOPICS = "/brain/api/topics"


def create(caller: Caller, name: str, slug: str | None = None) -> dict[str, Any]:
    body: dict[str, str] = {"name": name}
    if slug is not None:
        body["slug"] = slug
    response = caller.post(TOPICS, body)
    assert response.status_code == 201, response.text
    topic: dict[str, Any] = response.json()
    return topic


def create_body(caller: Caller, body: object) -> dict[str, Any]:
    response = caller.post(TOPICS, body)
    assert response.status_code == 201, response.text
    topic: dict[str, Any] = response.json()
    return topic


def listed(caller: Caller) -> dict[str, dict[str, Any]]:
    """The caller's topics, by slug."""
    response = caller.get(TOPICS)
    assert response.status_code == 200, response.text
    return {topic["slug"]: topic for topic in response.json()}


def same_as(caller: Caller, topic: dict[str, Any], target: dict[str, Any]) -> Any:
    return caller.post(f"{TOPICS}/{topic['id']}/same-as", {"target_id": target["id"]})


def insert_provisional(database: str, user: uuid.UUID, slug: str) -> uuid.UUID:
    """A provisional topic, the kind only an event creates, which arrives in
    a later slice. Written as the superuser, since no route makes one."""
    topic_id = uuid.uuid7()
    with admin_connection(database) as conn:
        conn.execute(
            "INSERT INTO topics (id, user_id, slug, name, state, alias_of, created_at)"
            " VALUES (%s, %s, %s, %s, 'provisional', NULL, now())",
            (topic_id, user, slug, slug),
        )
    return topic_id


def test_a_topic_i_create_is_canonical(calling: Callable[[uuid.UUID], Caller]) -> None:
    alice = calling(ALICE)

    topic = create(alice, "Postgres indexing", slug="postgres-indexing")

    assert topic["slug"] == "postgres-indexing"
    assert topic["name"] == "Postgres indexing"
    assert topic["state"] == "canonical"
    assert topic["alias_of"] is None
    assert topic["resolves_to"] == topic["id"]
    assert uuid.UUID(topic["id"]).version == 7
    assert listed(alice) == {"postgres-indexing": topic}


@pytest.mark.parametrize(
    ("name", "slug"),
    [
        ("Postgres indexing", "postgres-indexing"),
        ("  Rust & C++: ownership!  ", "rust-c-ownership"),
        ("B-tree indexes, 2026", "b-tree-indexes-2026"),
        ("a" * 79 + " b", "a" * 79),
    ],
)
def test_without_a_slug_it_is_derived_from_the_name(
    name: str, slug: str, calling: Callable[[uuid.UUID], Caller]
) -> None:
    assert create(calling(ALICE), name)["slug"] == slug


@pytest.mark.parametrize("name", ["日本語", "!!!", "", "   "])
def test_a_name_with_no_slug_in_it_is_refused(
    name: str, calling: Callable[[uuid.UUID], Caller]
) -> None:
    response = calling(ALICE).post(TOPICS, {"name": name})

    assert response.status_code == 422
    assert listed(calling(ALICE)) == {}


def test_a_slug_is_unique_per_user_not_across_users(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice, bob = calling(ALICE), calling(BOB)
    create(alice, "Postgres indexing", slug="postgres-indexing")

    # Another user's vocabulary is their own.
    create(bob, "Postgres indexing", slug="postgres-indexing")

    again = alice.post(
        TOPICS, {"name": "Indexes in Postgres", "slug": "postgres-indexing"}
    )
    assert again.status_code == 409
    assert list(listed(alice)) == ["postgres-indexing"]
    assert list(listed(bob)) == ["postgres-indexing"]


@pytest.mark.parametrize(
    "slug",
    [
        "Postgres-indexing",
        "postgres indexing",
        "",
        "-postgres",
        "a" * 81,
        "postgres_db",
    ],
    ids=["uppercase", "space", "empty", "leading hyphen", "too long", "underscore"],
)
def test_a_malformed_slug_is_refused(
    slug: str, calling: Callable[[uuid.UUID], Caller]
) -> None:
    alice = calling(ALICE)

    response = alice.post(TOPICS, {"name": "Postgres indexing", "slug": slug})

    assert response.status_code == 422
    assert listed(alice) == {}


def test_a_slug_of_eighty_characters_is_taken(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    # The control for "too long" above.
    assert create(calling(ALICE), "Long", slug="a" * 80)["slug"] == "a" * 80


def test_marking_a_topic_the_same_as_another_makes_it_an_alias(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    old = create(alice, "Database indexes")
    target = create(alice, "Postgres indexing")

    response = same_as(alice, old, target)

    assert response.status_code == 200, response.text
    alias = response.json()
    assert alias["alias_of"] == target["id"]
    assert alias["resolves_to"] == target["id"]
    # The old slug is still there, and resolves to the target.
    after = listed(alice)
    assert after["database-indexes"] == alias
    assert after["postgres-indexing"] == target
    # It still holds its slug, so the slug cannot be taken again.
    taken = alice.post(TOPICS, {"name": "Database indexes"})
    assert taken.status_code == 409


def test_an_alias_cannot_point_at_an_alias_or_at_itself(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    a = create(alice, "A")
    b = create(alice, "B")
    c = create(alice, "C")

    assert same_as(alice, a, a).status_code == 422
    assert same_as(alice, a, b).status_code == 200
    # At an alias.
    assert same_as(alice, c, a).status_code == 422
    # B has an alias, so making B one would leave A pointing at an alias.
    assert same_as(alice, b, c).status_code == 422
    # An alias is separated before it is pointed elsewhere.
    assert same_as(alice, a, c).status_code == 422

    after = listed(alice)
    assert after["a"]["alias_of"] == b["id"]
    assert after["b"]["alias_of"] is None
    assert after["c"]["alias_of"] is None


def test_an_alias_points_at_a_topic_that_exists(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    a = create(alice, "A")

    response = same_as(alice, a, {"id": str(uuid.uuid7())})

    assert response.status_code == 422
    assert listed(alice)["a"]["alias_of"] is None


def test_an_alias_points_at_a_canonical_topic(
    database: str, calling: Callable[[uuid.UUID], Caller]
) -> None:
    # "Merged into a canonical topic", in "Topic vocabulary".
    alice = calling(ALICE)
    a = create(alice, "A")
    provisional = insert_provisional(database, ALICE, "seen-once")

    response = same_as(alice, a, {"id": str(provisional)})

    assert response.status_code == 422


def test_separating_an_alias_makes_it_its_own_topic_again(
    database: str, calling: Callable[[uuid.UUID], Caller]
) -> None:
    alice = calling(ALICE)
    alias = create(alice, "Database indexes")
    target = create(alice, "Postgres indexing")
    broader = create(alice, "Databases")
    # Something attached to the alias: a relation naming it, written as the
    # superuser so this test needs nothing but topics.
    relation_id = uuid.uuid7()
    with admin_connection(database) as conn:
        conn.execute(
            "INSERT INTO topic_relations"
            " (id, user_id, from_topic, to_topic, kind, created_at, ended_at)"
            " VALUES (%s, %s, %s, %s, 'broader_than', now(), NULL)",
            (relation_id, ALICE, uuid.UUID(broader["id"]), uuid.UUID(alias["id"])),
        )
    assert same_as(alice, alias, target).status_code == 200

    response = alice.post(f"{TOPICS}/{alias['id']}/separate")

    assert response.status_code == 200, response.text
    assert response.json() == alias
    assert listed(alice)["database-indexes"] == alias
    # Nothing that pointed at it changed: the relation still names it.
    with admin_connection(database) as conn:
        row = conn.execute(
            "SELECT from_topic, to_topic, kind, ended_at FROM topic_relations WHERE id = %s",
            (relation_id,),
        ).fetchone()
    assert row == (
        uuid.UUID(broader["id"]),
        uuid.UUID(alias["id"]),
        "broader_than",
        None,
    )


def test_separating_a_topic_that_is_not_an_alias_changes_nothing(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic = create(alice, "Postgres indexing")

    response = alice.post(f"{TOPICS}/{topic['id']}/separate")

    assert response.status_code == 200
    assert response.json() == topic


def test_a_provisional_topic_can_be_promoted(
    database: str, calling: Callable[[uuid.UUID], Caller]
) -> None:
    alice = calling(ALICE)
    provisional = insert_provisional(database, ALICE, "b-tree-indexes")
    assert listed(alice)["b-tree-indexes"]["state"] == "provisional"

    response = alice.post(f"{TOPICS}/{provisional}/promote")

    assert response.status_code == 200, response.text
    assert response.json()["state"] == "canonical"
    assert listed(alice)["b-tree-indexes"]["state"] == "canonical"
    # Promoting a canonical topic leaves it canonical.
    again = alice.post(f"{TOPICS}/{provisional}/promote")
    assert again.status_code == 200
    assert again.json()["state"] == "canonical"


def test_an_alias_is_not_promoted(calling: Callable[[uuid.UUID], Caller]) -> None:
    alice = calling(ALICE)
    a = create(alice, "A")
    b = create(alice, "B")
    assert same_as(alice, a, b).status_code == 200

    assert alice.post(f"{TOPICS}/{a['id']}/promote").status_code == 422


def test_a_topic_that_is_not_there_is_404(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    create(alice, "A")
    missing = uuid.uuid7()

    for path, body in [
        (f"{TOPICS}/{missing}/promote", None),
        (f"{TOPICS}/{missing}/same-as", {"target_id": str(uuid.uuid7())}),
        (f"{TOPICS}/{missing}/separate", None),
        (f"{TOPICS}/{missing}/broader", {"broader_id": str(uuid.uuid7())}),
        (f"{TOPICS}/not-a-uuid/promote", None),
        (f"/brain/api/topic-relations/{missing}/end", None),
    ]:
        assert alice.post(path, body).status_code == 404, path


@pytest.mark.parametrize(
    "body",
    [{}, {"name": 42}, {"name": "A", "slug": 42}, "not an object", None],
    ids=["no name", "name not text", "slug not text", "not an object", "no body"],
)
def test_a_body_is_checked(
    body: object, calling: Callable[[uuid.UUID], Caller]
) -> None:
    alice = calling(ALICE)

    assert alice.post(TOPICS, body).status_code == 422
    assert listed(alice) == {}


def test_a_null_slug_is_derived_and_an_unknown_field_is_ignored(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    topic = create_body(calling(ALICE), {"name": "A", "slug": None, "extra": "x"})

    assert topic["slug"] == "a"


def test_a_body_that_is_not_json_is_422(serving: TestClient) -> None:
    caller = Caller(serving, ALICE)
    response = serving.post(
        TOPICS,
        content=b"{not json",
        headers={**caller.headers, "origin": "https://platform.test"},
    )

    assert response.status_code == 422


# Every route for the subjects, as a method, a path and a body.
ROUTES: list[tuple[str, str, object]] = [
    ("GET", TOPICS, None),
    ("POST", TOPICS, {"name": "A"}),
    ("POST", f"{TOPICS}/{uuid.uuid7()}/promote", None),
    ("POST", f"{TOPICS}/{uuid.uuid7()}/same-as", {"target_id": str(uuid.uuid7())}),
    ("POST", f"{TOPICS}/{uuid.uuid7()}/separate", None),
    ("POST", f"{TOPICS}/{uuid.uuid7()}/broader", {"broader_id": str(uuid.uuid7())}),
    ("POST", f"/brain/api/topic-relations/{uuid.uuid7()}/end", None),
    ("GET", "/brain/api/entities", None),
    ("POST", "/brain/api/entities", {"name": "Personal Platform"}),
]


@pytest.mark.parametrize(("method", "path", "body"), ROUTES)
@pytest.mark.parametrize("body_kind", ["valid", "malformed"])
def test_every_route_needs_a_valid_token(
    method: str, path: str, body: object, body_kind: str, serving: TestClient
) -> None:
    # Checked before the body, so a malformed body without a token is 401 too.
    content = b"{not json" if body_kind == "malformed" else None
    response = serving.request(
        method,
        path,
        headers={"origin": "https://platform.test"},
        json=body if content is None else None,
        content=content,
    )

    assert response.status_code == 401
    assert response.content == b""


async def test_changes_to_the_vocabulary_wait_for_each_other(
    pool: AsyncConnectionPool, database: str
) -> None:
    # The checks that keep an alias off an alias and the relations free of
    # cycles read, then write. Each change holds the user's lock for its
    # transaction, so two changes cannot both pass their checks on what the
    # other is about to change. Shown directly: while the lock is held, a
    # change waits, and goes on once it is released.
    now = datetime.now(UTC)
    async with db.transaction(pool, ALICE) as conn:
        a = await topics.create(conn, "A", "a", now=now)
        b = await topics.create(conn, "B", "b", now=now)

    async with await psycopg.AsyncConnection.connect(app_url(database)) as holder:
        async with holder.transaction():
            await holder.execute(
                "SELECT set_config('brain.user_id', %s, true)", (str(ALICE),)
            )
            await topics.lock_vocabulary(holder)

            async def change() -> None:
                async with db.transaction(pool, ALICE) as conn:
                    await topics.same_as(conn, a.id, b.id)

            waiting = asyncio.create_task(change())
            done, _ = await asyncio.wait({waiting}, timeout=1.0)
            assert done == set(), "the change did not wait for the lock"
        await asyncio.wait_for(waiting, timeout=10.0)

    async with db.transaction(pool, ALICE) as conn:
        assert await topics.resolve(conn, a.id) == b.id
