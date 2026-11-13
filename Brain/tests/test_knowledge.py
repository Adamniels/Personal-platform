"""What the owner knows: a level and a focus on each topic, misconceptions as
history entries on one, and the knowledge view over all of them, through the
API on a database of the test's own, as brain_app. The model is "How level is
represented" and "Seeding the knowledge model" in
`Brain/docs/knowledge-model.md`; why level and focus live in two stores is
"Authority follows the store, not the source" in `Brain/docs/trust-model.md`."""

import asyncio
import uuid
from collections.abc import Callable
from datetime import UTC, datetime
from typing import Any

import pytest
from psycopg_pool import AsyncConnectionPool

from brain import db, items
from tests.conftest import Caller
from tests.harness import app_url
from tests.test_corrections import act, read
from tests.test_items import ALICE, BOB, ITEMS, TOPICS, counts, listed, write

KNOWLEDGE = "/brain/api/knowledge"

LEVELS = ["shallow", "working", "solid", "deep"]
FOCUSES = ["learning", "steady", "not_pursuing"]


def topic(caller: Caller, name: str, slug: str) -> dict[str, Any]:
    response = caller.post(TOPICS, {"name": name, "slug": slug})
    assert response.status_code == 201, response.text
    created: dict[str, Any] = response.json()
    return created


def level(slug: str, value: str) -> dict[str, Any]:
    return {"kind": "topic_level", "topic": slug, "value": value}


def focus(slug: str, value: str) -> dict[str, Any]:
    return {"kind": "topic_focus", "topic": slug, "value": value}


def misconception(slug: str, believed: str, correct: str) -> dict[str, Any]:
    return {
        "kind": "history",
        "topic": slug,
        "details": {"misconception": True, "believed": believed, "correct": correct},
    }


def knowledge(caller: Caller, query: str = "") -> list[dict[str, Any]]:
    response = caller.get(f"{KNOWLEDGE}{query}")
    assert response.status_code == 200, response.text
    found: list[dict[str, Any]] = response.json()
    return found


def active(caller: Caller, kind: str, slug: str) -> list[dict[str, Any]]:
    return listed(caller, f"?kind={kind}&status=active&topic={slug}")


def test_a_level_is_one_of_four_and_lives_in_the_knowledge_store(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    for value in LEVELS:
        topic(alice, f"Topic {value}", value)

        written = write(alice, level(value, value))

        assert (written["kind"], written["store"], written["nature"]) == (
            "topic_level",
            "knowledge",
            "claim",
        )
        assert written["provenance"] == "asserted"
        assert written["status"] == "active"
        assert written["value"] == value
        assert written["content"] == f"Level for Topic {value}: {value}"
        assert written["category"] is None
        assert [t["slug"] for t in written["topics"]] == [value]
        assert written["entities"] == []
        [evidence] = written["evidence"]
        assert evidence["kind"] == "supports"
        assert evidence["event"]["verb"] == "asserted"

    before = counts(database)
    for wrong in ["expert", "Solid", "", "not_pursuing"]:
        response = alice.post(ITEMS, level("solid", wrong))
        assert response.status_code == 422, (wrong, response.text)
    assert counts(database) == before


def test_a_focus_is_one_of_three_and_lives_in_profile(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    for value in FOCUSES:
        slug = value.replace("_", "-")
        topic(alice, f"Topic {slug}", slug)

        written = write(alice, focus(slug, value))

        assert (written["kind"], written["store"], written["nature"]) == (
            "topic_focus",
            "profile",
            "claim",
        )
        assert written["value"] == value
        assert written["content"] == f"Focus for Topic {slug}: {value}"
        assert written["category"] is None
        assert [t["slug"] for t in written["topics"]] == [slug]

    before = counts(database)
    for wrong in ["not pursuing", "Learning", "", "solid"]:
        response = alice.post(ITEMS, focus("steady", wrong))
        assert response.status_code == 422, (wrong, response.text)
    assert counts(database) == before


@pytest.mark.parametrize("kind", ["topic_level", "topic_focus"])
def test_a_level_and_a_focus_each_need_exactly_one_topic(
    kind: str, calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    topic(alice, "Python", "python")
    value = "solid" if kind == "topic_level" else "learning"
    before = counts(database)

    wrong: list[dict[str, Any]] = [
        # No topic.
        {"kind": kind, "value": value},
        {"kind": kind, "topic": None, "value": value},
        # More than one, in any shape.
        {"kind": kind, "topic": ["rust", "python"], "value": value},
        {"kind": kind, "topics": ["rust", "python"], "value": value},
        {"kind": kind, "topic": "rust", "topics": ["python"], "value": value},
        # A named thing is not a topic.
        {"kind": kind, "topic": "rust", "entities": ["Rollout"], "value": value},
        # No value.
        {"kind": kind, "topic": "rust"},
        # The content is generated, never given.
        {"kind": kind, "topic": "rust", "value": value, "content": "Knows it."},
    ]
    for body in wrong:
        response = alice.post(ITEMS, body)
        assert response.status_code == 422, (body, response.text)

    # The topic must exist: a level never makes one.
    response = alice.post(ITEMS, {"kind": kind, "topic": "haskell", "value": value})
    assert response.status_code == 422, response.text
    assert "haskell" in response.json()["detail"]
    # Nor does another user's topic count.
    bob = calling(BOB)
    response = bob.post(ITEMS, {"kind": kind, "topic": "rust", "value": value})
    assert response.status_code == 422, response.text

    assert counts(database) == before


def test_a_topic_has_at_most_one_active_level_and_one_active_focus(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    first = write(alice, level("rust", "working"))
    first_focus = write(alice, focus("rust", "learning"))

    second = write(alice, level("rust", "solid"))

    # An edit: a new version of the same entry, not a second entry.
    assert second["id"] != first["id"]
    assert second["lineage_id"] == first["lineage_id"] == first["id"]
    assert second["value"] == "solid"
    assert second["content"] == "Level for Rust: solid"
    assert [r["kind"] for r in second["relations"]] == ["supersedes"]
    assert read(alice, first["id"])["inactive_reason"] == "superseded"
    assert [i["id"] for i in active(alice, "topic_level", "rust")] == [second["id"]]
    # The focus is an entry of its own, and the level did not touch it.
    assert [i["id"] for i in active(alice, "topic_focus", "rust")] == [
        first_focus["id"]
    ]
    second_focus = write(alice, focus("rust", "steady"))
    assert second_focus["lineage_id"] == first_focus["id"]
    assert [i["id"] for i in active(alice, "topic_focus", "rust")] == [
        second_focus["id"]
    ]

    # Setting what is already set changes nothing, as any such edit.
    before = counts(database)
    response = alice.post(ITEMS, level("rust", "solid"))
    assert response.status_code == 422, response.text
    assert counts(database) == before

    # A retired level is not active, so the next one is a new entry; and
    # the retired one cannot come back beside it.
    act(alice, second["id"], "retire")
    third = write(alice, level("rust", "deep"))
    assert third["lineage_id"] == third["id"] != first["lineage_id"]
    response = alice.post(f"{ITEMS}/{second['id']}/restore")
    assert response.status_code == 409, response.text
    assert [i["id"] for i in active(alice, "topic_level", "rust")] == [third["id"]]

    # Each user's topics are their own.
    bob = calling(BOB)
    topic(bob, "Rust", "rust")
    bobs = write(bob, level("rust", "shallow"))
    assert bobs["lineage_id"] == bobs["id"]


async def test_two_levels_set_at_once_make_one_entry(database: str) -> None:
    """The second waits for the first and then edits it, rather than both
    finding none and each writing an entry."""
    pool = AsyncConnectionPool(app_url(database), min_size=2, max_size=2, open=False)
    await pool.open(wait=True)
    try:
        now = datetime.now(UTC)
        async with db.transaction(pool, ALICE) as conn:
            await conn.execute(
                "INSERT INTO topics (id, user_id, slug, name, state, alias_of,"
                " created_at) VALUES (%s, %s, 'rust', 'Rust', 'canonical', NULL, %s)",
                (uuid.uuid7(), ALICE, now),
            )

        async def set_level(value: str) -> uuid.UUID:
            async with db.transaction(pool, ALICE) as conn:
                return await items.set_on_topic(
                    conn, "topic_level", "rust", value, now=datetime.now(UTC)
                )

        async with db.transaction(pool, ALICE) as conn:
            first = await items.set_on_topic(
                conn, "topic_level", "rust", "working", now=now
            )
            second = asyncio.create_task(set_level("solid"))
            await asyncio.sleep(0.3)
            # Held by the first, which has not committed.
            assert not second.done()
        second_id = await second

        async with db.transaction(pool, ALICE) as conn:
            found = await items.listing(
                conn, kind="topic_level", status=None, topic="rust", entity=None
            )
        assert {(i.id, i.status) for i in found} == {
            (first, "inactive"),
            (second_id, "active"),
        }
        assert {i.lineage_id for i in found} == {first}
    finally:
        await pool.close()


def test_setting_a_level_on_an_alias_sets_it_on_the_topic_it_stands_for(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    target = topic(alice, "Rust", "rust")
    alias = topic(alice, "Rust lang", "rust-lang")
    response = alice.post(
        f"{TOPICS}/{alias['id']}/same-as", {"target_id": target["id"]}
    )
    assert response.status_code == 200, response.text
    first = write(alice, level("rust", "working"))

    through_alias = write(alice, level("rust-lang", "solid"))

    assert [t["slug"] for t in through_alias["topics"]] == ["rust"]
    assert through_alias["content"] == "Level for Rust: solid"
    assert through_alias["lineage_id"] == first["id"]
    # A focus set first through the alias is on the target too.
    on_alias = write(alice, focus("rust-lang", "learning"))
    assert [t["slug"] for t in on_alias["topics"]] == ["rust"]
    assert [i["id"] for i in active(alice, "topic_focus", "rust")] == [on_alias["id"]]
    # Separated again, the alias has neither: they were set on the target.
    response = alice.post(f"{TOPICS}/{alias['id']}/separate")
    assert response.status_code == 200, response.text
    [row] = [r for r in knowledge(alice) if r["slug"] == "rust-lang"]
    assert row["level"] is None and row["focus"] is None


def test_changing_a_level_keeps_the_history_of_levels(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    first = write(alice, level("rust", "working"))
    second = write(alice, level("rust", "solid"))

    response = alice.get(f"{ITEMS}/{second['id']}/versions")
    assert response.status_code == 200, response.text
    versions = response.json()

    assert [(v["id"], v["value"], v["status"]) for v in versions] == [
        (first["id"], "working", "inactive"),
        (second["id"], "solid", "active"),
    ]
    # Each with its date and the event that made it.
    for version, written in zip(versions, [first, second], strict=True):
        assert version["created_at"] == written["created_at"]
        assert version["created_by_event"] == written["created_by_event"]
    assert first["created_at"] < second["created_at"]
    # The change itself is a judgment against the old level, quoting both.
    [edited] = [
        e
        for e in read(alice, first["id"])["evidence"]
        if e["event"]["verb"] == "edited"
    ]
    assert edited["kind"] == "contradicts"
    assert edited["event"]["evidence"] == (
        'Corrected by hand from "Level for Rust: working" to "Level for Rust: solid"'
    )


def test_a_misconception_is_a_history_entry_on_the_topic(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    before = datetime.now(UTC)

    written = write(
        alice,
        misconception(
            "rust",
            "  A move copies the value.  ",
            "A move transfers ownership; only Copy types are copied.",
        ),
    )

    assert (written["kind"], written["store"], written["nature"]) == (
        "history",
        "reported_history",
        "record",
    )
    assert written["details"] == {
        "misconception": True,
        "believed": "A move copies the value.",
        "correct": "A move transfers ownership; only Copy types are copied.",
    }
    assert written["content"] == (
        "Misconception about Rust\n"
        "Believed: A move copies the value.\n"
        "Correct: A move transfers ownership; only Copy types are copied."
    )
    assert [t["slug"] for t in written["topics"]] == ["rust"]
    assert written["value"] is None and written["category"] is None
    # A record says when: when it was entered, unless told otherwise.
    assert written["occurred_precision"] == "instant"
    assert before <= datetime.fromisoformat(written["occurred_at"]) <= datetime.now(UTC)
    dated = write(
        alice,
        {
            **misconception("rust", "Lifetimes are runtime.", "They are compile time."),
            "occurred_at": "2024-03-01T00:00:00Z",
            "occurred_precision": "month",
        },
    )
    assert dated["occurred_at"] == "2024-03-01T00:00:00Z"
    assert dated["occurred_precision"] == "month"
    assert dated["evidence"][0]["event"]["occurred_at"] == "2024-03-01T00:00:00Z"

    before_counts = counts(database)
    wrong: list[dict[str, Any]] = [
        # Not a misconception, which is all a history entry is so far.
        {
            "kind": "history",
            "topic": "rust",
            "details": {"believed": "x", "correct": "y"},
        },
        {
            "kind": "history",
            "topic": "rust",
            "details": {"misconception": False, "believed": "x", "correct": "y"},
        },
        # Both halves, each with text.
        {"kind": "history", "topic": "rust", "details": {"misconception": True}},
        misconception("rust", "   ", "y"),
        misconception("rust", "x", ""),
        # Something else in details.
        {
            "kind": "history",
            "topic": "rust",
            "details": {"misconception": True, "believed": "x", "correct": "y", "z": 1},
        },
        # Exactly one topic, which exists.
        {
            "kind": "history",
            "details": {"misconception": True, "believed": "x", "correct": "y"},
        },
        {**misconception("rust", "x", "y"), "topics": ["rust"]},
        misconception("haskell", "x", "y"),
        # The content is built, never given.
        {**misconception("rust", "x", "y"), "content": "Wrong about moves."},
    ]
    for body in wrong:
        response = alice.post(ITEMS, body)
        assert response.status_code == 422, (body, response.text)
    assert counts(database) == before_counts


def test_the_knowledge_view_lists_every_topic_with_its_level_and_focus(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    rust = topic(alice, "Rust", "rust")
    alias = topic(alice, "Rust lang", "rust-lang")
    response = alice.post(f"{TOPICS}/{alias['id']}/same-as", {"target_id": rust["id"]})
    assert response.status_code == 200, response.text
    python = topic(alice, "Python", "python")
    # A provisional topic, made by an event, is listed too.
    response = alice.post(
        "/brain/api/events",
        {
            "verb": "opened",
            "topic": "temporal",
            "origin": "news",
            "evidence": "Opened an article about Temporal.",
            "payload": {},
            "payload_version": 1,
            "idempotency_key": "a1",
        },
    )
    assert response.status_code == 201, response.text
    write(alice, level("rust", "working"))
    rust_level = write(alice, level("rust", "solid"))
    rust_focus = write(alice, focus("rust-lang", "learning"))
    write(alice, misconception("rust", "A move copies.", "A move transfers."))
    on_alias = write(alice, misconception("rust-lang", "Lifetimes run.", "They don't."))
    retired = write(alice, misconception("rust", "Retired.", "Retired."))
    act(alice, retired["id"], "retire")
    # Another user's are never counted.
    bob = calling(BOB)
    topic(bob, "Rust", "rust")
    write(bob, level("rust", "deep"))

    rows = knowledge(alice)

    # One row per topic, aliases folded into their target, by slug.
    assert [r["slug"] for r in rows] == ["python", "rust", "temporal"]
    by_slug = {r["slug"]: r for r in rows}
    assert by_slug["rust"] == {
        "id": rust["id"],
        "slug": "rust",
        "name": "Rust",
        "state": "canonical",
        "level": {
            "id": rust_level["id"],
            "value": "solid",
            "set_at": rust_level["created_at"],
        },
        "focus": {
            "id": rust_focus["id"],
            "value": "learning",
            "set_at": rust_focus["created_at"],
        },
        # Active history entries, the alias's included, the retired not.
        "history_count": 2,
    }
    assert on_alias["topics"][0]["slug"] == "rust"
    # A topic with neither is listed, with both empty.
    assert by_slug["python"] == {
        "id": python["id"],
        "slug": "python",
        "name": "Python",
        "state": "canonical",
        "level": None,
        "focus": None,
        "history_count": 0,
    }
    assert by_slug["temporal"]["state"] == "provisional"
    assert by_slug["temporal"]["level"] is None
    # Bob sees his own, and nothing of Alice's.
    [bobs] = knowledge(bob)
    assert (bobs["slug"], bobs["level"]["value"], bobs["history_count"]) == (
        "rust",
        "deep",
        0,
    )
    # Without a token, nothing.
    assert alice.client.get(KNOWLEDGE).status_code == 401


def test_the_knowledge_view_filters_by_level_and_focus(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    for slug in ["rust", "python", "csharp", "react"]:
        topic(alice, slug.title(), slug)
    write(alice, level("rust", "solid"))
    write(alice, focus("rust", "learning"))
    write(alice, level("python", "solid"))
    write(alice, focus("python", "steady"))
    write(alice, level("csharp", "deep"))
    write(alice, focus("react", "not_pursuing"))
    response = alice.post(
        "/brain/api/events",
        {
            "verb": "opened",
            "topic": "temporal",
            "origin": "news",
            "evidence": "Opened an article about Temporal.",
            "payload": {},
            "payload_version": 1,
            "idempotency_key": "a1",
        },
    )
    assert response.status_code == 201, response.text

    def slugs(query: str) -> list[str]:
        return [r["slug"] for r in knowledge(alice, query)]

    assert slugs("?level=solid") == ["python", "rust"]
    assert slugs("?level=deep") == ["csharp"]
    assert slugs("?level=shallow") == []
    assert slugs("?focus=learning") == ["rust"]
    assert slugs("?focus=not_pursuing") == ["react"]
    assert slugs("?level=solid&focus=steady") == ["python"]
    assert slugs("?state=provisional") == ["temporal"]
    assert slugs("?state=canonical&level=deep") == ["csharp"]
    assert slugs("") == ["csharp", "python", "react", "rust", "temporal"]
    for wrong in ["?level=expert", "?focus=solid", "?state=alias"]:
        response = alice.get(f"{KNOWLEDGE}{wrong}")
        assert response.status_code == 422, (wrong, response.text)
