"""Conflicts and related topics: asking about a topic brings the topics under
it, and two items the owner marked as contradicting come back as a conflict,
both kept, ordered and never quietly decided. Through the API on a database
of the test's own, as brain_app, except the scope comparison, which is a
pure function. Why scopes form a partial order is "Scope is topic and
entity, never feature" in `Brain/docs/trust-model.md`; why recency only
orders is "Conflicting decisions before consolidation" in
`Brain/docs/event-model.md`."""

import itertools
import uuid
from collections.abc import Callable
from typing import Any

from brain.scope import Scope, compare
from tests.conftest import Caller
from tests.harness import admin_connection
from tests.test_ask import ASK_TOPICS, HISTORY, ask, ids
from tests.test_items import ALICE, write
from tests.test_knowledge import level, topic
from tests.test_now_and_history import history
from tests.test_topic_relations import end, under

TOPICS = "/brain/api/topics"


def related(caller: Caller, one: dict[str, Any], other: dict[str, Any]) -> Any:
    return caller.post(f"{TOPICS}/{one['id']}/related", {"related_id": other["id"]})


def topic_answer(caller: Caller, slug: str, mode: str = "now") -> dict[str, Any]:
    [answer] = ask(caller, ASK_TOPICS, {"topics": [slug], "mode": mode})["topics"]
    entry: dict[str, Any] = answer
    return entry


def by_slug(entries: list[dict[str, Any]]) -> dict[str, dict[str, Any]]:
    return {entry["topic"]["slug"]: entry for entry in entries}


def databases_tree(caller: Caller) -> dict[str, dict[str, Any]]:
    """databases, over postgres, over postgres-indexing; the relations by
    the narrower topic's slug."""
    databases = topic(caller, "Databases", "databases")
    postgres = topic(caller, "Postgres", "postgres")
    indexing = topic(caller, "Postgres indexing", "postgres-indexing")
    return {
        "postgres": under(caller, postgres, databases).json(),
        "postgres-indexing": under(caller, indexing, postgres).json(),
    }


# Narrower and related topics.


def test_asking_about_a_topic_includes_the_topics_under_it(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    databases_tree(alice)
    known = write(alice, level("postgres-indexing", "working"))
    learnt = write(alice, history(topics=["postgres-indexing"]))
    own = write(alice, history(topics=["databases"], content="Read a databases book."))

    answer = topic_answer(alice, "databases")

    assert ids(answer["history"]) == [own["id"]]
    narrower = by_slug(answer["narrower"])
    # Nearest first, each marked with the chain of slugs that led to it.
    assert [n["topic"]["slug"] for n in answer["narrower"]] == [
        "postgres",
        "postgres-indexing",
    ]
    assert narrower["postgres"]["via"] == ["databases"]
    assert narrower["postgres-indexing"]["via"] == ["databases", "postgres"]
    indexing = narrower["postgres-indexing"]
    assert indexing["level"]["id"] == known["id"]
    assert ids(indexing["history"]) == [learnt["id"]]
    assert indexing["focus"] is None and indexing["misconceptions"] == []

    # The history of the broader topic holds the narrower one's too.
    told = ask(alice, HISTORY, {"topic": "databases"})
    assert ids(told["items"]) == [own["id"]]
    told_narrower = by_slug(told["narrower"])
    assert told_narrower["postgres-indexing"]["via"] == ["databases", "postgres"]
    assert ids(told_narrower["postgres-indexing"]["items"]) == [learnt["id"]]
    assert told_narrower["postgres"]["items"] == []

    # Asked about the narrow topic, the broad one is not under it.
    assert topic_answer(alice, "postgres-indexing")["narrower"] == []


def test_an_ended_relation_no_longer_brings_the_narrower_topic(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    relations = databases_tree(alice)
    write(alice, history(topics=["postgres-indexing"]))

    assert end(alice, relations["postgres"]).status_code == 200

    assert topic_answer(alice, "databases")["narrower"] == []
    assert ask(alice, HISTORY, {"topic": "databases"})["narrower"] == []
    # What is still under postgres is still under it.
    assert [
        n["topic"]["slug"] for n in topic_answer(alice, "postgres")["narrower"]
    ] == ["postgres-indexing"]


def test_narrower_topics_are_followed_to_any_depth_without_looping(
    database: str, calling: Callable[[uuid.UUID], Caller]
) -> None:
    alice = calling(ALICE)
    chain = [topic(alice, f"Level {n}", f"level-{n}") for n in range(6)]
    for broader, narrower in itertools.pairwise(chain):
        assert under(alice, narrower, broader).status_code == 201
    # A second, longer way to level-3 through a side topic: it is listed
    # once, by the shortest chain.
    side = topic(alice, "Side", "side")
    assert under(alice, side, chain[0]).status_code == 201
    assert under(alice, chain[3], side).status_code == 201
    deepest = write(alice, history(topics=["level-5"]))

    answer = topic_answer(alice, "level-0")

    narrower = by_slug(answer["narrower"])
    assert set(narrower) == {
        "level-1",
        "level-2",
        "level-3",
        "level-4",
        "level-5",
        "side",
    }
    assert len(answer["narrower"]) == 6
    assert narrower["level-3"]["via"] == ["level-0", "side"]
    assert narrower["level-5"]["via"] == ["level-0", "side", "level-3", "level-4"]
    assert ids(narrower["level-5"]["history"]) == [deepest["id"]]

    # A loop the API refuses to make, written as the superuser: the walk
    # still ends, and a topic is never under itself.
    with admin_connection(database) as conn:
        conn.execute(
            "INSERT INTO topic_relations"
            " (id, user_id, from_topic, to_topic, kind, created_at, ended_at)"
            " VALUES (%s, %s, %s, %s, 'broader_than', now(), NULL)",
            (uuid.uuid7(), ALICE, uuid.UUID(chain[5]["id"]), uuid.UUID(chain[0]["id"])),
        )
    looped = topic_answer(alice, "level-2")
    assert [n["topic"]["slug"] for n in looped["narrower"]] == [
        "level-3",
        "level-4",
        "level-5",
        "level-0",
        "level-1",
        "side",
    ]
    assert by_slug(looped["narrower"])["level-1"]["via"] == [
        "level-2",
        "level-3",
        "level-4",
        "level-5",
        "level-0",
    ]


def test_a_related_topic_is_listed_but_its_items_are_not_pulled_in(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    databases = topic(alice, "Databases", "databases")
    caching = topic(alice, "Caching", "caching")
    queues = topic(alice, "Queues", "queues")
    write(alice, history(topics=["caching"]))
    response = related(alice, databases, caching)
    assert response.status_code == 201, response.text
    assert response.json()["kind"] == "related_to"
    # Made from the other side, it is related all the same.
    assert related(alice, queues, databases).status_code == 201

    answer = topic_answer(alice, "databases")

    assert answer["related"] == ["caching", "queues"]
    assert answer["narrower"] == [] and answer["history"] == []
    told = ask(alice, HISTORY, {"topic": "databases"})
    assert told["related"] == ["caching", "queues"]
    assert told["items"] == [] and told["narrower"] == []
    # Related either way, and only once.
    assert topic_answer(alice, "caching")["related"] == ["databases"]
    assert related(alice, caching, databases).status_code == 409
    assert related(alice, databases, databases).status_code == 422
    # Ended, it is listed no more.
    assert end(alice, response.json()).status_code == 200
    assert topic_answer(alice, "databases")["related"] == ["queues"]


# The scope comparison.


def test_a_scope_is_narrower_when_it_holds_every_qualifier_of_the_other_and_more() -> (
    None
):
    postgres, indexing, alias = uuid.uuid7(), uuid.uuid7(), uuid.uuid7()
    rollout = uuid.uuid7()
    global_ = Scope.of([], [])
    on_postgres = Scope.of([postgres], [])
    on_both = Scope.of([postgres], [rollout])

    assert compare(on_both, on_postgres) == "narrower"
    assert compare(on_postgres, on_both) == "broader"
    assert compare(on_postgres, global_) == "narrower"
    assert compare(global_, on_postgres) == "broader"
    assert compare(global_, Scope.of([], [])) == "equal"
    assert compare(on_postgres, Scope.of([postgres], [])) == "equal"
    # A topic and a named thing, or two topics, neither holding the other.
    assert compare(on_postgres, Scope.of([], [rollout])) == "incomparable"
    assert compare(on_postgres, Scope.of([indexing], [])) == "incomparable"
    assert compare(Scope.of([postgres, indexing], []), on_both) == "incomparable"
    # Aliases resolved first: an alias of postgres is postgres.
    aliased = Scope.of([alias], [], alias_of={alias: postgres})
    assert compare(aliased, on_postgres) == "equal"
    assert compare(
        Scope.of([alias, postgres], [], alias_of={alias: postgres}), on_postgres
    ) == ("equal")
    assert compare(on_both, aliased) == "narrower"
    # A topic and a named thing are never the same qualifier, whatever ids.
    same = uuid.uuid7()
    assert compare(Scope.of([same], []), Scope.of([], [same])) == "incomparable"
