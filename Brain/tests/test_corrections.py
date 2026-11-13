"""Correcting and retiring: an edit as a new version, retiring an item and
bringing it back, and the versions of an entry, through the API on a database
of the test's own, as brain_app. The rules are "Nothing is destroyed, and only
the owner retires" and "Editing changes provenance, and every edit records a
judgment" in `Brain/docs/trust-model.md`, and "Versions" in
`Brain/docs/data-model.md`."""

import uuid
from collections.abc import Callable
from datetime import UTC, datetime
from typing import Any

import pytest
from psycopg_pool import AsyncConnectionPool

from brain import db, items
from tests.conftest import Caller
from tests.harness import admin_connection
from tests.test_items import ALICE, ITEMS, TOPICS, counts, profile, rule, write

EVENTS = "/brain/api/events"


def edit(caller: Caller, item_id: str, body: dict[str, Any]) -> dict[str, Any]:
    response = caller.post(f"{ITEMS}/{item_id}/edit", body)
    assert response.status_code == 201, response.text
    edited: dict[str, Any] = response.json()
    return edited


def act(caller: Caller, item_id: str, action: str) -> dict[str, Any]:
    response = caller.post(f"{ITEMS}/{item_id}/{action}")
    assert response.status_code == 200, response.text
    acted: dict[str, Any] = response.json()
    return acted


def read(caller: Caller, item_id: str) -> dict[str, Any]:
    response = caller.get(f"{ITEMS}/{item_id}")
    assert response.status_code == 200, response.text
    found: dict[str, Any] = response.json()
    return found


def timeline(caller: Caller) -> list[dict[str, Any]]:
    response = caller.get(EVENTS)
    assert response.status_code == 200, response.text
    found: list[dict[str, Any]] = response.json()
    return found


def about(caller: Caller, item_id: str, verb: str) -> list[dict[str, Any]]:
    """The events with this verb whose subject is this item."""
    return [
        event
        for event in timeline(caller)
        if event["verb"] == verb and event["subject_item"] == item_id
    ]


def test_an_edit_creates_a_new_version_and_supersedes_the_old(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    old = write(alice, profile(content="Prefers tabs."))

    new = edit(alice, old["id"], {"kind": "profile", "content": "  Prefers spaces. "})

    assert new["id"] != old["id"]
    assert new["lineage_id"] == old["lineage_id"] == old["id"]
    assert new["status"] == "active" and new["inactive_reason"] is None
    assert new["provenance"] == "asserted"
    assert new["content"] == "Prefers spaces."
    assert new["category"] == old["category"]
    assert (new["kind"], new["store"], new["nature"]) == ("profile", "profile", "claim")
    superseded = read(alice, old["id"])
    assert superseded["status"] == "inactive"
    assert superseded["inactive_reason"] == "superseded"
    # The old row is as it was, but for its status.
    assert superseded["content"] == "Prefers tabs."
    [relation] = new["relations"]
    assert relation["kind"] == "supersedes"
    assert (relation["from_item"], relation["to_item"]) == (new["id"], old["id"])
    assert relation["ended_at"] is None
    assert superseded["relations"] == [relation]
    with admin_connection(database) as conn:
        rows = conn.execute(
            "SELECT id, status, inactive_reason FROM items WHERE lineage_id = %s"
            " ORDER BY created_at",
            (old["id"],),
        ).fetchall()
    assert rows == [
        (uuid.UUID(old["id"]), "inactive", "superseded"),
        (uuid.UUID(new["id"]), "active", None),
    ]


def test_an_edit_of_a_rule_is_a_new_version_too(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    old = write(alice, rule(content="Answer in Swedish."))

    new = edit(alice, old["id"], {"kind": "rule", "content": "Answer in English."})

    assert new["lineage_id"] == old["id"]
    assert (new["kind"], new["store"], new["content"]) == (
        "rule",
        "procedural",
        "Answer in English.",
    )
    assert read(alice, old["id"])["inactive_reason"] == "superseded"


def test_an_edit_records_a_judgment_against_the_original(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    old = write(alice, profile(content="Prefers tabs."))

    new = edit(alice, old["id"], {"kind": "profile", "content": "Prefers spaces."})

    [edited] = about(alice, old["id"], "edited")
    assert edited["origin"] == "brain" and edited["source"] == "manual-entry"
    assert (
        edited["evidence"]
        == 'Corrected by hand from "Prefers tabs." to "Prefers spaces."'
    )
    # The judgment is evidence against the original, and made the relation.
    superseded = read(alice, old["id"])
    assert [(e["kind"], e["event"]["id"]) for e in superseded["evidence"]][-1] == (
        "contradicts",
        edited["id"],
    )
    assert new["relations"][0]["created_by_event"] == edited["id"]
    # The new version is made by an asserted event, its supporting evidence.
    [asserted] = [
        event
        for event in timeline(alice)
        if event["verb"] == "asserted" and event["id"] == new["created_by_event"]
    ]
    assert asserted["origin"] == "brain" and asserted["source"] == "manual-entry"
    assert [(e["kind"], e["event"]["id"]) for e in new["evidence"]] == [
        ("supports", asserted["id"])
    ]


def test_an_edits_evidence_cuts_each_text_to_200_characters(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    old = write(alice, profile(content="a" * 300))

    edit(alice, old["id"], {"kind": "profile", "content": "b" * 300})

    [edited] = about(alice, old["id"], "edited")
    cut_old, cut_new = "a" * 199 + "…", "b" * 199 + "…"
    assert edited["evidence"] == f'Corrected by hand from "{cut_old}" to "{cut_new}"'


def test_an_edit_keeps_the_subjects_unless_it_changes_them(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    for name in ["Rust", "Embedded"]:
        assert alice.post(TOPICS, {"name": name}).status_code == 201
    first = write(
        alice, profile(topics=["rust"], entities=["Personal Platform"], content="One.")
    )

    def subjects(item: dict[str, Any]) -> tuple[list[str], list[str]]:
        return (
            [topic["slug"] for topic in item["topics"]],
            [entity["name"] for entity in item["entities"]],
        )

    # Content alone: both kinds of subject carried over.
    second = edit(alice, first["id"], {"kind": "profile", "content": "Two."})
    assert subjects(second) == (["rust"], ["Personal Platform"])
    # The topics alone: the named things carried over.
    third = edit(alice, second["id"], {"kind": "profile", "topics": ["embedded"]})
    assert subjects(third) == (["embedded"], ["Personal Platform"])
    assert third["content"] == "Two."
    # The named things alone, emptied: the topics carried over.
    fourth = edit(alice, third["id"], {"kind": "profile", "entities": []})
    assert subjects(fourth) == (["embedded"], [])
    # And the category, alone.
    fifth = edit(alice, fourth["id"], {"kind": "profile", "category": "goals"})
    assert fifth["category"] == "goals" and subjects(fifth) == (["embedded"], [])
    # Every earlier version keeps its own subjects.
    assert subjects(read(alice, first["id"])) == (["rust"], ["Personal Platform"])


def test_only_the_newest_version_can_be_edited(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    old = write(alice, profile(content="Prefers tabs."))
    edit(alice, old["id"], {"kind": "profile", "content": "Prefers spaces."})
    before = counts(database)

    response = alice.post(
        f"{ITEMS}/{old['id']}/edit", {"kind": "profile", "content": "Prefers both."}
    )

    assert response.status_code == 409, response.text
    assert counts(database) == before


@pytest.mark.parametrize(
    "body",
    [
        {"kind": "profile"},
        {"kind": "profile", "content": "Prefers tabs."},
        {"kind": "profile", "content": "  Prefers tabs.\n", "category": "values"},
        {"kind": "profile", "topics": [], "entities": []},
    ],
)
def test_an_edit_that_changes_nothing_is_refused(
    body: dict[str, Any], calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    item = write(alice, profile(content="Prefers tabs.", category="values"))
    before = counts(database)

    response = alice.post(f"{ITEMS}/{item['id']}/edit", body)

    assert response.status_code == 422, response.text
    assert counts(database) == before
    assert read(alice, item["id"])["status"] == "active"


@pytest.mark.parametrize(
    "body",
    [
        # The item is a profile entry, not a rule.
        {"kind": "rule", "content": "Something else."},
        {"content": "Something else."},
        {"kind": "profile", "content": "   "},
        {"kind": "profile", "content": "x" * 4001},
        {"kind": "profile", "category": "skills"},
        {"kind": "profile", "topics": ["haskell"]},
        {"kind": "profile", "content": "x", "mood": "happy"},
    ],
)
def test_a_malformed_edit_is_refused(
    body: dict[str, Any], calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    item = write(alice, profile())
    before = counts(database)

    response = alice.post(f"{ITEMS}/{item['id']}/edit", body)

    assert response.status_code == 422, response.text
    assert counts(database) == before


def test_a_rule_edit_names_no_subject(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    item = write(alice, rule())

    response = alice.post(
        f"{ITEMS}/{item['id']}/edit", {"kind": "rule", "entities": ["Rust"]}
    )

    assert response.status_code == 422, response.text


async def test_all_of_it_happens_in_one_transaction(
    pool: AsyncConnectionPool, database: str, monkeypatch: pytest.MonkeyPatch
) -> None:
    now = datetime.now(UTC)
    new = items.NewItem(
        kind="profile",
        content="Prefers tabs.",
        category="values",
        topics=(),
        entities=("Operation Rollout",),
        occurred_at=None,
        occurred_precision="instant",
    )
    async with db.transaction(pool, ALICE) as conn:
        item_id = await items.write(conn, new, now=now)
    before = counts(database)

    class Boom(Exception):
        pass

    async def fails(*args: object, **kwargs: object) -> None:
        raise Boom

    # The last step fails, after the old version is superseded and the new
    # one, its subjects, its events and its evidence are in.
    monkeypatch.setattr(items, "_link_relation", fails)
    with pytest.raises(Boom):
        async with db.transaction(pool, ALICE) as conn:
            await items.edit(
                conn,
                item_id,
                items.Edit(
                    kind="profile",
                    content="Prefers spaces.",
                    category=None,
                    topics=None,
                    entities=("Personal Platform",),
                ),
                now=now,
            )

    assert counts(database) == before
    with admin_connection(database) as admin:
        assert admin.execute(
            "SELECT status, inactive_reason, content FROM items WHERE id = %s",
            (item_id,),
        ).fetchone() == ("active", None, "Prefers tabs.")
        # The named thing the failed edit would have made is not there either.
        assert admin.execute("SELECT count(*) FROM entities").fetchone() == (1,)
