"""Asking the brain: the four questions under `/brain/api/ask/`, through the
API on a database of the test's own, as brain_app. Each answer is built from
the building blocks in `brain.retrieval`, and every item in it carries the
reasons it is believed. Why each item says why is "Retrieval: per feature
calls over shared primitives" in `Brain/docs/brain-architecture.md`; why now
and history read currency differently is "Confidence, currency and status
are three different things" in `Brain/docs/trust-model.md`."""

import ast
import inspect
import uuid
from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

from brain import retrieval
from tests.conftest import Caller, bearer, mint
from tests.test_corrections import act, edit
from tests.test_items import (
    ALICE,
    BOB,
    CATEGORIES,
    ENTITIES,
    TOPICS,
    counts,
    profile,
    rule,
    write,
)
from tests.test_knowledge import focus, level, misconception, topic
from tests.test_now_and_history import decision, history, iso, replace, state, when

ASK = "/brain/api/ask"
PROFILE = f"{ASK}/profile"
ASK_TOPICS = f"{ASK}/topics"
HISTORY = f"{ASK}/history"
NOW = f"{ASK}/now"

# What every item in every answer carries, and nothing else.
ITEM_FIELDS = {
    "id",
    "kind",
    "store",
    "nature",
    "provenance",
    "status",
    "inactive_reason",
    "content",
    "value",
    "category",
    "details",
    "occurred_at",
    "occurred_precision",
    "subjects",
    "last_supported_at",
    "stale",
    "support",
    "evidence",
}
EVIDENCE_FIELDS = {
    "verb",
    "kind",
    "text",
    "origin",
    "occurred_at",
    "occurred_precision",
    "recorded_at",
}
VERB_KINDS = {"exposure", "engagement", "judgment", "assertion", "decision", "activity"}


def ask(caller: Caller, path: str, body: dict[str, Any]) -> dict[str, Any]:
    response = caller.post(path, body)
    assert response.status_code == 200, response.text
    answer: dict[str, Any] = response.json()
    return answer


def ids(found: list[dict[str, Any]]) -> list[str]:
    return [each["id"] for each in found]


def every_item(caller: Caller, mode: str, slugs: list[str]) -> list[dict[str, Any]]:
    """Every item the four questions answer with in `mode`, asking about each
    of `slugs` as topics and as a history subject."""
    found: list[dict[str, Any]] = []
    answered = ask(caller, PROFILE, {"mode": mode})
    for group in answered["categories"]:
        found.extend(group["items"])
    found.extend(answered["rules"])
    if slugs:
        for each in ask(caller, ASK_TOPICS, {"topics": slugs, "mode": mode})["topics"]:
            found.extend(item for item in (each["level"], each["focus"]) if item)
            found.extend(each["misconceptions"])
            found.extend(each["history"])
    for slug in slugs:
        found.extend(ask(caller, HISTORY, {"topic": slug, "mode": mode})["items"])
    found.extend(ask(caller, NOW, {"mode": mode})["items"])
    return found


def test_ask_profile_returns_active_profile_entries_and_rules(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    identity = write(alice, profile(category="identity", content="A student."))
    goal = write(alice, profile(category="goals", content="An internship."))
    corrected = edit(alice, goal["id"], {"kind": "profile", "content": "A job."})
    interest = write(alice, profile(category="interests", content="Systems."))
    retired = write(alice, profile(category="values", content="Put away."))
    act(alice, retired["id"], "retire")
    first_rule = write(alice, rule(content="Be direct."))
    second_rule = write(alice, rule(content="No filler."))
    # Not who he is: what is going on now, and a focus, which a topic's
    # answer carries.
    write(alice, state())
    write(alice, focus("rust", "learning"))

    answer = ask(alice, PROFILE, {})

    assert answer["mode"] == "now"
    # Every category, in the order the profile lists them, each grouped.
    assert [group["category"] for group in answer["categories"]] == CATEGORIES
    grouped = {g["category"]: ids(g["items"]) for g in answer["categories"]}
    assert grouped == {
        "identity": [identity["id"]],
        "goals": [corrected["id"]],
        "values": [],
        "interests": [interest["id"]],
        "preferences": [],
        "working_style": [],
    }
    # Rules after, newest support first, as the mode now orders.
    assert ids(answer["rules"]) == [second_rule["id"], first_rule["id"]]
    every = [i for g in answer["categories"] for i in g["items"]] + answer["rules"]
    assert {i["status"] for i in every} == {"active"}
    assert {i["kind"] for i in every} == {"profile", "rule"}
    assert [i["category"] for i in answer["rules"]] == [None, None]


def test_ask_topics_returns_level_focus_and_history_for_each_topic(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    topic(alice, "Go", "go")
    write(alice, level("rust", "solid"))
    deeper = write(alice, level("rust", "deep"))
    rust_focus = write(alice, focus("rust", "learning"))
    wrong = write(alice, misconception("rust", "Rc is thread safe.", "Arc is."))
    course = write(alice, history(content="Took a Rust course.", topics=["rust"]))
    put_away = write(alice, history(content="Put away.", topics=["rust"]))
    act(alice, put_away["id"], "retire")
    go_level = write(alice, level("go", "working"))
    # About the topic, but not what he knows of it.
    write(alice, state(topics=["rust"]))
    write(alice, decision(topics=["rust"]))
    write(alice, profile(topics=["rust"]))

    answer = ask(alice, ASK_TOPICS, {"topics": ["rust", "go"]})

    assert answer["mode"] == "now"
    rust, go = answer["topics"]
    assert rust["asked"] == "rust" and rust["known"] is True
    assert rust["topic"]["slug"] == "rust" and rust["topic"]["name"] == "Rust"
    # The level it has now, not the one it had.
    assert rust["level"]["id"] == deeper["id"]
    assert rust["level"]["value"] == "deep"
    assert rust["focus"]["id"] == rust_focus["id"]
    assert rust["focus"]["value"] == "learning"
    assert ids(rust["misconceptions"]) == [wrong["id"]]
    assert rust["misconceptions"][0]["details"]["believed"] == "Rc is thread safe."
    assert ids(rust["history"]) == [course["id"]]

    assert go["asked"] == "go" and go["topic"]["slug"] == "go"
    assert go["level"]["id"] == go_level["id"]
    assert go["focus"] is None
    assert go["misconceptions"] == [] and go["history"] == []

    # The order asked, whatever it is.
    reversed_answer = ask(alice, ASK_TOPICS, {"topics": ["go", "rust"]})
    assert [t["asked"] for t in reversed_answer["topics"]] == ["go", "rust"]


def test_ask_topics_resolves_an_alias_to_its_topic(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    kubernetes = topic(alice, "Kubernetes", "kubernetes")
    k8s = topic(alice, "K8s", "k8s")
    # Named on the alias while it was its own topic.
    wrong = write(
        alice, misconception("k8s", "Pods are containers.", "They hold some.")
    )
    response = alice.post(
        f"{TOPICS}/{k8s['id']}/same-as", {"target_id": kubernetes["id"]}
    )
    assert response.status_code == 200, response.text
    solid = write(alice, level("k8s", "solid"))

    answer = ask(alice, ASK_TOPICS, {"topics": ["k8s", "kubernetes"]})

    by_alias, by_target = answer["topics"]
    assert by_alias["asked"] == "k8s"
    assert by_alias["topic"]["slug"] == "kubernetes"
    assert by_alias["topic"]["id"] == kubernetes["id"]
    assert by_alias["known"] is True
    assert by_alias["level"]["id"] == solid["id"]
    assert ids(by_alias["misconceptions"]) == [wrong["id"]]
    # The same topic answers, whichever slug asked.
    assert by_target["asked"] == "kubernetes"
    assert {k: v for k, v in by_target.items() if k != "asked"} == {
        k: v for k, v in by_alias.items() if k != "asked"
    }


def test_ask_topics_about_an_unknown_topic_answers_with_nothing_known(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    write(alice, level("rust", "working"))
    before = counts(database)
    topics_before = alice.get(TOPICS).json()

    answer = ask(alice, ASK_TOPICS, {"topics": ["haskell", "rust"]})

    haskell, rust = answer["topics"]
    assert haskell == {
        "asked": "haskell",
        "topic": None,
        "known": False,
        "level": None,
        "focus": None,
        "misconceptions": [],
        "history": [],
    }
    assert rust["known"] is True and rust["level"]["value"] == "working"
    # Asking never creates a topic, nor anything else.
    assert alice.get(TOPICS).json() == topics_before
    assert counts(database) == before

    # A list of 1 to 50 slugs, each well formed.
    assert alice.post(ASK_TOPICS, {"topics": []}).status_code == 422
    assert alice.post(ASK_TOPICS, {"topics": ["a"] * 50}).status_code == 200
    assert alice.post(ASK_TOPICS, {"topics": ["a"] * 51}).status_code == 422
    assert alice.post(ASK_TOPICS, {"topics": ["Not A Slug"]}).status_code == 422
    assert alice.post(ASK_TOPICS, {}).status_code == 422


def test_ask_history_returns_records_and_decisions_for_a_subject(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    course = write(
        alice,
        history(
            content="Took a Rust course.",
            occurred_at="2023-03-01T00:00:00Z",
            topics=["rust"],
        ),
    )
    first = write(
        alice,
        decision(
            content="The core is in Rust.",
            occurred_at="2025-01-10T00:00:00Z",
            topics=["rust"],
            entities=["Platform"],
        ),
    )
    second = write(
        alice,
        decision(
            content="The core stays in Rust, async.",
            occurred_at="2025-06-01T00:00:00Z",
            topics=["rust"],
            entities=["Platform"],
        ),
    )
    assert replace(alice, first["id"], second["id"]).status_code == 200
    put_away = write(
        alice, history(content="Put away.", topics=["rust"], entities=["Platform"])
    )
    act(alice, put_away["id"], "retire")
    # About the subject, but neither a record nor a decision.
    write(alice, state(topics=["rust"], entities=["Platform"]))
    write(alice, profile(topics=["rust"]))
    # A record about something else.
    write(alice, history(content="Elsewhere."))

    answer = ask(alice, HISTORY, {"topic": "rust", "mode": "history"})

    assert answer["mode"] == "history"
    assert answer["asked"] == "rust" and answer["known"] is True
    assert answer["topic"]["slug"] == "rust" and answer["entity"] is None
    assert ids(answer["items"]) == [course["id"], first["id"], second["id"]]
    replaced, standing = answer["items"][1:]
    # The replaced decision is there, marked as no longer holding.
    assert replaced["status"] == "inactive"
    assert replaced["inactive_reason"] == "superseded"
    assert standing["status"] == "active" and standing["inactive_reason"] is None

    # A named thing, by name in any letter case.
    by_thing = ask(alice, HISTORY, {"entity": "platform", "mode": "history"})
    assert by_thing["asked"] == "platform" and by_thing["known"] is True
    assert by_thing["entity"]["name"] == "Platform" and by_thing["topic"] is None
    assert ids(by_thing["items"]) == [first["id"], second["id"]]

    # Asking about now leaves out what no longer holds.
    assert ids(ask(alice, HISTORY, {"topic": "rust"})["items"]) == [
        second["id"],
        course["id"],
    ]

    # A subject the user does not have: nothing known, and nothing created.
    before = counts(database)
    entities_before = alice.get(ENTITIES).json()
    for body in ({"topic": "haskell"}, {"entity": "Nothing by this name"}):
        unknown = ask(alice, HISTORY, body)
        assert unknown["known"] is False and unknown["items"] == []
        assert unknown["topic"] is None and unknown["entity"] is None
    assert alice.get(ENTITIES).json() == entities_before
    assert counts(database) == before

    # One subject, exactly.
    assert alice.post(HISTORY, {}).status_code == 422
    both = {"topic": "rust", "entity": "Platform"}
    assert alice.post(HISTORY, both).status_code == 422


def test_ask_now_returns_current_state(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    first = write(alice, state(content="Building the brain.", topics=["rust"]))
    second = write(alice, state(content="Reading about Postgres."))
    put_away = write(alice, state(content="Put away."))
    act(alice, put_away["id"], "retire")
    write(alice, profile())
    write(alice, rule())
    write(alice, history())
    write(alice, decision())
    write(alice, level("rust", "solid"))

    answer = ask(alice, NOW, {})

    assert answer["mode"] == "now"
    assert ids(answer["items"]) == [second["id"], first["id"]]
    assert {i["kind"] for i in answer["items"]} == {"state"}
    assert {i["status"] for i in answer["items"]} == {"active"}
    [rust] = answer["items"][1]["subjects"]["topics"]
    assert rust["slug"] == "rust"


def test_every_returned_item_carries_its_reasons(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    said = datetime(2026, 1, 5, 9, 0, tzinfo=UTC)
    doing = write(
        alice,
        state(
            content="Building the brain.",
            topics=["rust"],
            entities=["Platform"],
            occurred_at=iso(said),
            occurred_precision="day",
        ),
    )
    act(alice, doing["id"], "confirm")
    who = write(alice, profile(category="identity", content="A student."))
    act(alice, who["id"], "retire")
    act(alice, who["id"], "restore")
    write(alice, rule())
    write(alice, level("rust", "solid"))
    write(alice, misconception("rust", "x", "y"))
    write(alice, decision(topics=["rust"]))

    everything = every_item(alice, "now", ["rust"]) + every_item(
        alice, "history", ["rust"]
    )

    assert {i["kind"] for i in everything} == {
        "state",
        "profile",
        "rule",
        "topic_level",
        "history",
        "decision",
    }
    for item in everything:
        assert set(item) == ITEM_FIELDS, item
        assert set(item["subjects"]) == {"topics", "entities"}
        assert set(item["support"]) == {"supports", "contradicts"}
        for side in item["support"].values():
            assert set(side) == VERB_KINDS
        assert item["evidence"], item
        for each in item["evidence"]:
            assert set(each) == EVIDENCE_FIELDS
            assert each["text"] and each["verb"] and each["origin"]
        supporting = [e for e in item["evidence"] if e["kind"] == "supports"]
        assert when(item["last_supported_at"]) == max(
            when(e["occurred_at"]) for e in supporting
        )

    [state_now] = ask(alice, NOW, {})["items"]
    assert state_now["id"] == doing["id"]
    assert state_now["store"] == "semantic" and state_now["nature"] == "claim"
    assert state_now["provenance"] == "asserted"
    assert state_now["content"] == "Building the brain."
    assert [t["slug"] for t in state_now["subjects"]["topics"]] == ["rust"]
    assert [n["name"] for n in state_now["subjects"]["entities"]] == ["Platform"]
    asserted, confirmed = state_now["evidence"]
    assert asserted["verb"] == "asserted" and asserted["kind"] == "supports"
    assert asserted["text"] == 'Entered by hand as state: "Building the brain."'
    assert asserted["origin"] == "brain"
    assert when(asserted["occurred_at"]) == said
    assert asserted["occurred_precision"] == "day"
    assert when(asserted["recorded_at"]) > said
    assert confirmed["verb"] == "confirmed" and confirmed["kind"] == "supports"
    assert state_now["support"]["supports"] == {
        "exposure": 0,
        "engagement": 0,
        "judgment": 1,
        "assertion": 1,
        "decision": 0,
        "activity": 0,
    }
    assert sum(state_now["support"]["contradicts"].values()) == 0
    assert state_now["last_supported_at"] == confirmed["occurred_at"]

    # Evidence against is counted and shown, not hidden.
    [group] = [
        g for g in ask(alice, PROFILE, {})["categories"] if g["category"] == "identity"
    ]
    [restored] = group["items"]
    assert [(e["verb"], e["kind"]) for e in restored["evidence"]] == [
        ("asserted", "supports"),
        ("retired", "contradicts"),
        ("restored", "supports"),
    ]
    assert restored["support"]["supports"]["assertion"] == 1
    assert restored["support"]["supports"]["judgment"] == 1
    assert restored["support"]["contradicts"]["judgment"] == 1


def test_asking_about_now_puts_newest_support_first_and_flags_stale(
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

    items = ask(alice, NOW, {"mode": "now"})["items"]

    assert ids(items) == [recent["id"], middle["id"], old["id"]]
    assert [i["stale"] for i in items] == [False, False, True]
    assert [when(i["last_supported_at"]) for i in items] == [
        now - timedelta(days=2),
        now - timedelta(days=30),
        now - timedelta(days=90),
    ]

    # Confirming the oldest brings it to the front, and clears its flag.
    act(alice, old["id"], "confirm")
    items = ask(alice, NOW, {"mode": "now"})["items"]
    assert ids(items) == [old["id"], recent["id"], middle["id"]]
    assert [i["stale"] for i in items] == [False, False, False]

    # The profile is ordered the same way, though it is never flagged.
    older = write(
        alice,
        profile(category="identity", occurred_at=iso(now - timedelta(days=400))),
    )
    newer = write(alice, profile(category="identity", content="Newer."))
    [identity] = [
        g for g in ask(alice, PROFILE, {})["categories"] if g["category"] == "identity"
    ]
    assert ids(identity["items"]) == [newer["id"], older["id"]]
    assert [i["stale"] for i in identity["items"]] == [False, False]


def test_asking_about_history_ignores_age(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    now = datetime.now(UTC).replace(microsecond=0)
    old = write(alice, state(content="Old.", occurred_at=iso(now - timedelta(days=90))))
    recent = write(
        alice, state(content="Recent.", occurred_at=iso(now - timedelta(days=2)))
    )
    middle = write(
        alice, state(content="Middle.", occurred_at=iso(now - timedelta(days=30)))
    )
    # Entered in the opposite order to when they happened.
    decided = write(
        alice, decision(occurred_at=iso(now - timedelta(days=7)), topics=["rust"])
    )
    course = write(
        alice,
        history(
            content="Took a Rust course.",
            occurred_at="2015-03-01T00:00:00Z",
            topics=["rust"],
        ),
    )

    items = ask(alice, NOW, {"mode": "history"})["items"]

    assert ids(items) == [old["id"], middle["id"], recent["id"]]
    assert [i["stale"] for i in items] == [False, False, False]

    answer = ask(alice, HISTORY, {"topic": "rust", "mode": "history"})
    assert ids(answer["items"]) == [course["id"], decided["id"]]
    assert [i["stale"] for i in answer["items"]] == [False, False]

    rust = ask(alice, ASK_TOPICS, {"topics": ["rust"], "mode": "history"})["topics"]
    assert ids(rust[0]["history"]) == [course["id"]]

    for item in every_item(alice, "history", ["rust"]):
        assert item["stale"] is False

    assert alice.post(NOW, {"mode": "later"}).status_code == 422


def test_no_answer_ever_contains_a_retired_item_as_active(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    topic(alice, "Rust", "rust")
    retired = [
        write(alice, profile(topics=["rust"])),
        write(alice, rule()),
        write(alice, state(topics=["rust"])),
        write(alice, level("rust", "solid")),
        write(alice, focus("rust", "learning")),
        write(alice, misconception("rust", "x", "y")),
        write(alice, history(topics=["rust"])),
        write(alice, decision(topics=["rust"])),
    ]
    for item in retired:
        act(alice, item["id"], "retire")
    kept = write(alice, profile(content="Kept.", topics=["rust"]))
    corrected = edit(alice, kept["id"], {"kind": "profile", "content": "Corrected."})
    standing = write(alice, state(content="Standing.", topics=["rust"]))

    gone = {item["id"] for item in retired} | {kept["id"]}
    for mode in ("now", "history"):
        found = every_item(alice, mode, ["rust"])
        assert {item["id"] for item in found} == {corrected["id"], standing["id"]}
        assert not gone & {item["id"] for item in found}
        assert all(item["inactive_reason"] != "retired" for item in found)
        assert all(item["status"] == "active" for item in found)
        [rust] = ask(alice, ASK_TOPICS, {"topics": ["rust"], "mode": mode})["topics"]
        assert rust["level"] is None and rust["focus"] is None


def test_an_answer_never_crosses_users(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    bob = calling(BOB)
    topic(alice, "Rust", "rust")
    write(alice, profile(topics=["rust"], entities=["Platform"]))
    write(alice, rule())
    write(alice, state(topics=["rust"]))
    write(alice, level("rust", "solid"))
    write(alice, history(topics=["rust"], entities=["Platform"]))
    write(alice, decision(topics=["rust"]))
    assert every_item(alice, "now", ["rust"])

    assert every_item(bob, "now", []) == []
    assert every_item(bob, "history", []) == []
    [rust] = ask(bob, ASK_TOPICS, {"topics": ["rust"]})["topics"]
    assert rust["known"] is False and rust["topic"] is None
    subjects: list[dict[str, Any]] = [{"topic": "rust"}, {"entity": "Platform"}]
    for body in subjects:
        assert ask(bob, HISTORY, body) == {
            "mode": "now",
            "asked": body.get("topic") or body.get("entity"),
            "known": False,
            "topic": None,
            "entity": None,
            "items": [],
        }

    # No field of a question names a user: whatever tries is refused.
    bodies: dict[str, dict[str, Any]] = {
        PROFILE: {},
        ASK_TOPICS: {"topics": ["rust"]},
        HISTORY: {"topic": "rust"},
        NOW: {},
    }
    for path, body in bodies.items():
        for field in ("user_id", "account_id", "user"):
            response = bob.post(path, {**body, field: str(ALICE)})
            assert response.status_code == 422, (path, field, response.text)

    # The user is the token's, and a question without one is not answered.
    for path, body in bodies.items():
        response = bob.client.post(
            path, json=body, headers={"origin": "https://platform.test"}
        )
        assert response.status_code == 401, path
        assert response.content == b""
        # Like any other POST, it comes from the platform's origin.
        response = bob.client.post(path, json=body, headers=bearer(mint(BOB)))
        assert response.status_code == 403, path


def test_no_answer_makes_a_model_call_or_leaves_the_database() -> None:
    # Each building block takes the door's connection, the time, and its
    # question: no pool, client, settings, key or user.
    questions: dict[Callable[..., object], list[str]] = {
        retrieval.profile: [],
        retrieval.knowledge: ["topic_ids"],
        retrieval.history: ["subject"],
        retrieval.now: [],
    }
    for block, asked in questions.items():
        parameters = inspect.signature(block).parameters
        assert list(parameters) == ["conn", *asked, "now", "mode"], block
        assert parameters["now"].kind is inspect.Parameter.KEYWORD_ONLY
        assert parameters["now"].annotation is datetime
        assert parameters["conn"].annotation is retrieval.Connection
    # Search, the fifth, asks by words and reads as now does, so it has no
    # mode, but takes the same connection and time.
    parameters = inspect.signature(retrieval.search).parameters
    assert list(parameters)[:2] == ["conn", "query"]
    assert parameters["now"].kind is inspect.Parameter.KEYWORD_ONLY
    assert parameters["now"].annotation is datetime
    assert parameters["conn"].annotation is retrieval.Connection

    # And the module reaches nothing but the database through them: it
    # imports nothing that could call a model or the network, or read the
    # clock or the settings.
    source = Path(inspect.getfile(retrieval)).read_text()
    tree = ast.parse(source)
    imported: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            imported.update(alias.name for alias in node.names)
        elif isinstance(node, ast.ImportFrom):
            assert node.module is not None
            imported.update(f"{node.module}.{alias.name}" for alias in node.names)
    allowed = (
        "collections.",
        "dataclasses.",
        "datetime.datetime",
        "typing.",
        "uuid.",
        "psycopg.AsyncConnection",
        "psycopg.rows.",
        "brain.events",
        "brain.items",
        "brain.now_and_history",
    )
    assert imported and all(name.startswith(allowed) for name in imported), imported
    calls = {
        node.func.attr
        for node in ast.walk(tree)
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
    }
    assert not calls & {"now", "utcnow", "today", "time"}, calls
