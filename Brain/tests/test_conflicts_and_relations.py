"""Conflicts and related topics: asking about a topic brings the topics under
it, and two items the owner marked as contradicting come back as a conflict,
both kept, ordered and never quietly decided. Through the API on a database
of the test's own, as brain_app, except the scope comparison, which is a
pure function. Why scopes form a partial order is "Scope is topic and
entity, never feature" in `Brain/docs/trust-model.md`; why recency only
orders is "Conflicting decisions before consolidation" in
`Brain/docs/event-model.md`."""

import ast
import inspect
import itertools
import uuid
from collections.abc import Callable
from pathlib import Path
from typing import Any

from brain import scope
from brain.scope import Scope, compare
from tests.conftest import Caller
from tests.harness import admin_connection
from tests.test_ask import ASK_TOPICS, HISTORY, NOW, PROFILE, ask, ids
from tests.test_corrections import act, edit
from tests.test_items import ALICE, BOB, ITEMS, rule, write
from tests.test_knowledge import level, topic
from tests.test_now_and_history import decision, history, replace, state
from tests.test_topic_relations import end, under

TOPICS = "/brain/api/topics"
ITEM_RELATIONS = "/brain/api/item-relations"


def related(caller: Caller, one: dict[str, Any], other: dict[str, Any]) -> Any:
    return caller.post(f"{TOPICS}/{one['id']}/related", {"related_id": other["id"]})


def contradicts(caller: Caller, one: str, other: str) -> Any:
    return caller.post(f"{ITEMS}/{one}/contradicts", {"contradicts": other})


def marked(caller: Caller, one: str, other: str) -> dict[str, Any]:
    response = contradicts(caller, one, other)
    assert response.status_code == 201, response.text
    relation: dict[str, Any] = response.json()
    return relation


def take_back(caller: Caller, relation_id: str) -> Any:
    return caller.post(f"{ITEM_RELATIONS}/{relation_id}/end")


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


def test_the_scope_comparison_reads_nothing_but_its_arguments() -> None:
    # Pure: it imports no database, clock, settings or network, so what it
    # answers depends on the two scopes alone.
    tree = ast.parse(Path(inspect.getfile(scope)).read_text())
    imported = {
        f"{node.module}.{alias.name}"
        for node in ast.walk(tree)
        if isinstance(node, ast.ImportFrom)
        for alias in node.names
    }
    assert not any(isinstance(node, ast.Import) for node in ast.walk(tree))
    assert imported and all(
        name.startswith(("collections.abc.", "dataclasses.", "typing.", "uuid."))
        for name in imported
    ), imported


# Conflicts.


def test_two_decisions_marked_as_contradicting_come_back_as_a_conflict(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Postgres", "postgres")
    older = write(alice, decision(topics=["postgres"], content="Use one schema."))
    newer = write(alice, decision(topics=["postgres"], content="Use many schemas."))
    relation = marked(alice, older["id"], newer["id"])

    for mode in ("now", "history"):
        answer = ask(alice, HISTORY, {"topic": "postgres", "mode": mode})
        assert set(ids(answer["items"])) == {older["id"], newer["id"]}
        assert answer["conflicts"] == [
            {
                "items": [older["id"], newer["id"]],
                "prefer": newer["id"],
                "reason": "newer at equal scope",
            }
        ]
    assert relation["kind"] == "contradicts"
    assert (relation["from_item"], relation["to_item"]) == (older["id"], newer["id"])
    # Every answer carries its conflicts, an empty list when it has none.
    assert ask(alice, PROFILE, {})["conflicts"] == []
    assert ask(alice, NOW, {})["conflicts"] == []
    assert ask(alice, ASK_TOPICS, {"topics": ["postgres"]})["conflicts"] == []
    # Another user sees neither the items nor the conflict.
    bob = calling(BOB)
    assert ask(bob, HISTORY, {"topic": "postgres"})["conflicts"] == []


def test_at_equal_scope_the_newer_is_ranked_first_and_the_older_stays(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    older = write(alice, rule(content="Answer in full."))
    newer = write(alice, rule(content="Answer briefly."))
    other = write(alice, rule(content="Use metric units."))
    # Confirmed since, the older one is the newest supported, and would lead
    # by the order alone.
    act(alice, older["id"], "confirm")
    assert ids(ask(alice, PROFILE, {})["rules"]) == [
        older["id"],
        other["id"],
        newer["id"],
    ]

    marked(alice, newer["id"], older["id"])

    answer = ask(alice, PROFILE, {})
    assert ids(answer["rules"]) == [newer["id"], older["id"], other["id"]]
    assert answer["conflicts"] == [
        {
            "items": [newer["id"], older["id"]],
            "prefer": newer["id"],
            "reason": "newer at equal scope",
        }
    ]
    # Asked about history, the order is when each happened, and only that.
    told = ask(alice, PROFILE, {"mode": "history"})
    assert ids(told["rules"]) == [older["id"], newer["id"], other["id"]]
    assert told["conflicts"] == answer["conflicts"]


def test_a_narrower_scope_is_ranked_first_whatever_its_age(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Postgres", "postgres")
    narrow = write(
        alice,
        decision(
            topics=["postgres"],
            entities=["Operation Rollout"],
            content="Rollout keeps one schema.",
        ),
    )
    broad = write(alice, decision(topics=["postgres"], content="Use many schemas."))
    assert ids(ask(alice, HISTORY, {"topic": "postgres"})["items"]) == [
        broad["id"],
        narrow["id"],
    ]

    marked(alice, broad["id"], narrow["id"])

    answer = ask(alice, HISTORY, {"topic": "postgres"})
    assert ids(answer["items"]) == [narrow["id"], broad["id"]]
    assert answer["conflicts"] == [
        {
            "items": [broad["id"], narrow["id"]],
            "prefer": narrow["id"],
            "reason": "narrower scope",
        }
    ]


def test_incomparable_scopes_are_a_conflict_with_no_winner(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Postgres", "postgres")
    on_topic = write(alice, state(topics=["postgres"], content="Tuning Postgres."))
    on_thing = write(
        alice, state(entities=["Operation Rollout"], content="Not touching databases.")
    )
    before = ids(ask(alice, NOW, {})["items"])

    marked(alice, on_topic["id"], on_thing["id"])

    answer = ask(alice, NOW, {})
    assert ids(answer["items"]) == before
    assert answer["conflicts"] == [
        {
            "items": [on_topic["id"], on_thing["id"]],
            "prefer": None,
            "reason": "incomparable scopes",
        }
    ]


def test_a_superseded_item_is_never_part_of_a_conflict(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Postgres", "postgres")
    replaced = write(alice, decision(topics=["postgres"], content="Use one schema."))
    standing = write(alice, decision(topics=["postgres"], content="Use many schemas."))
    marked(alice, replaced["id"], standing["id"])
    replacing = write(alice, decision(topics=["postgres"], content="Use two schemas."))
    assert replace(alice, replaced["id"], replacing["id"]).status_code == 200
    edited_rule = write(alice, rule(content="Answer in full."))
    other_rule = write(alice, rule(content="Answer briefly."))
    marked(alice, edited_rule["id"], other_rule["id"])
    edit(alice, edited_rule["id"], {"kind": "rule", "content": "Answer in part."})

    # Asked about history, the replaced decision is in the answer, but not in
    # a conflict; nor is an edited rule's older version, nor its newest.
    told = ask(alice, HISTORY, {"topic": "postgres", "mode": "history"})
    assert replaced["id"] in ids(told["items"])
    assert told["conflicts"] == []
    assert ask(alice, HISTORY, {"topic": "postgres"})["conflicts"] == []
    assert ask(alice, PROFILE, {})["conflicts"] == []
    # And a superseded item cannot be marked.
    assert contradicts(alice, replaced["id"], replacing["id"]).status_code == 409


def test_i_can_mark_two_items_as_contradicting_and_take_it_back(
    database: str, calling: Callable[[uuid.UUID], Caller]
) -> None:
    alice = calling(ALICE)
    one = write(alice, rule(content="Answer in full."))
    other = write(alice, rule(content="Answer briefly."))

    relation = marked(alice, one["id"], other["id"])

    assert relation["ended_at"] is None
    detail = alice.get(f"{ITEMS}/{one['id']}").json()
    assert [r["id"] for r in detail["relations"]] == [relation["id"]]
    assert len(ask(alice, PROFILE, {})["conflicts"]) == 1
    # Marking is the owner's judgment of neither: no evidence on either.
    assert [e["event"]["verb"] for e in detail["evidence"]] == ["asserted"]
    with admin_connection(database) as conn:
        made = conn.execute(
            "SELECT e.verb, e.subject_item FROM item_relations r"
            " JOIN events e ON e.id = r.created_by_event WHERE r.id = %s",
            (uuid.UUID(relation["id"]),),
        ).fetchone()
    assert made == ("contradicted", uuid.UUID(one["id"]))

    response = take_back(alice, relation["id"])

    assert response.status_code == 200, response.text
    ended = response.json()
    assert ended["ended_at"] is not None
    assert {k: v for k, v in ended.items() if k != "ended_at"} == {
        k: v for k, v in relation.items() if k != "ended_at"
    }
    assert ask(alice, PROFILE, {})["conflicts"] == []
    # The row stays, ended, and ending it again answers it as it is.
    detail = alice.get(f"{ITEMS}/{one['id']}").json()
    assert detail["relations"] == [ended]
    again = take_back(alice, relation["id"])
    assert again.status_code == 200 and again.json() == ended
    # Taken back, the two can be marked again.
    assert contradicts(alice, other["id"], one["id"]).status_code == 201


def test_only_two_active_claims_can_be_marked_as_contradicting(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    one = write(alice, rule(content="Answer in full."))
    other = write(alice, rule(content="Answer briefly."))
    record = write(alice, history())
    retired = write(alice, rule(content="Put away."))
    act(alice, retired["id"], "retire")
    bobs = write(calling(BOB), rule(content="Bob's."))

    assert contradicts(alice, one["id"], record["id"]).status_code == 422
    assert contradicts(alice, record["id"], one["id"]).status_code == 422
    assert contradicts(alice, one["id"], one["id"]).status_code == 422
    assert contradicts(alice, one["id"], retired["id"]).status_code == 409
    assert contradicts(alice, retired["id"], one["id"]).status_code == 409
    assert contradicts(alice, str(uuid.uuid7()), one["id"]).status_code == 404
    assert contradicts(alice, one["id"], bobs["id"]).status_code == 422
    assert contradicts(alice, bobs["id"], one["id"]).status_code == 404
    assert alice.post(f"{ITEMS}/{one['id']}/contradicts", {}).status_code == 422
    marked(alice, one["id"], other["id"])
    # Once, either way round, while it holds.
    assert contradicts(alice, one["id"], other["id"]).status_code == 409
    assert contradicts(alice, other["id"], one["id"]).status_code == 409


def test_only_a_contradiction_is_taken_back(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    old = write(alice, decision(content="Use one schema."))
    new = write(alice, decision(content="Use many schemas."))
    detail = replace(alice, old["id"], new["id"]).json()
    [supersedes] = detail["relations"]
    assert supersedes["kind"] == "supersedes"

    assert take_back(alice, supersedes["id"]).status_code == 422
    assert take_back(alice, str(uuid.uuid7())).status_code == 404
    # Another user's contradiction is not there for Alice.
    bob = calling(BOB)
    bobs = marked(
        bob, write(bob, rule(content="A."))["id"], write(bob, rule(content="B."))["id"]
    )
    assert take_back(alice, bobs["id"]).status_code == 404
