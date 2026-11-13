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
from tests.test_items import ALICE, BOB, ITEMS, TOPICS, counts, profile, rule, write

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


def test_a_retired_item_is_brought_back_before_it_is_edited(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    item = write(alice, rule())
    act(alice, item["id"], "retire")
    before = counts(database)

    response = alice.post(
        f"{ITEMS}/{item['id']}/edit", {"kind": "rule", "content": "x"}
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


def test_retiring_makes_an_item_inactive_and_records_it(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    item = write(alice, profile(content="Prefers tabs."))

    retired = act(alice, item["id"], "retire")

    assert retired["id"] == item["id"]
    assert retired["status"] == "inactive" and retired["inactive_reason"] == "retired"
    assert retired["content"] == "Prefers tabs."
    [event] = about(alice, item["id"], "retired")
    assert event["origin"] == "brain" and event["source"] == "manual-entry"
    assert event["evidence"] == 'Retired by hand: "Prefers tabs."'
    assert [(e["kind"], e["event"]["id"]) for e in retired["evidence"]][-1] == (
        "contradicts",
        event["id"],
    )
    assert read(alice, item["id"]) == retired


def test_a_retired_item_can_be_brought_back(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    item = write(alice, rule(content="Concise prose."))
    act(alice, item["id"], "retire")

    restored = act(alice, item["id"], "restore")

    assert restored["status"] == "active" and restored["inactive_reason"] is None
    [event] = about(alice, item["id"], "restored")
    assert event["origin"] == "brain" and event["source"] == "manual-entry"
    assert event["evidence"] == 'Restored by hand: "Concise prose."'
    assert [(e["kind"], e["event"]["id"]) for e in restored["evidence"]][-1] == (
        "supports",
        event["id"],
    )
    # And it can be put away again, each time recorded.
    act(alice, item["id"], "retire")
    assert len(about(alice, item["id"], "retired")) == 2


def test_a_superseded_version_cannot_be_restored(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    old = write(alice, profile(content="Prefers tabs."))
    edit(alice, old["id"], {"kind": "profile", "content": "Prefers spaces."})
    before = counts(database)

    response = alice.post(f"{ITEMS}/{old['id']}/restore")

    assert response.status_code == 409, response.text
    assert counts(database) == before
    assert read(alice, old["id"])["inactive_reason"] == "superseded"


def test_only_an_active_item_is_retired_and_only_a_retired_one_restored(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    item = write(alice, rule())
    old = write(alice, profile(content="Prefers tabs."))
    edit(alice, old["id"], {"kind": "profile", "content": "Prefers spaces."})

    # An active item has nothing to come back from.
    assert alice.post(f"{ITEMS}/{item['id']}/restore").status_code == 409
    act(alice, item["id"], "retire")
    before = counts(database)
    # Retired is retired once.
    assert alice.post(f"{ITEMS}/{item['id']}/retire").status_code == 409
    # A superseded version is not the entry any more; its newest version is.
    assert alice.post(f"{ITEMS}/{old['id']}/retire").status_code == 409
    assert counts(database) == before


def test_another_users_item_is_not_there_to_change(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice, bob = calling(ALICE), calling(BOB)
    item = write(alice, rule())
    before = counts(database)

    for path, body in [
        ("edit", {"kind": "rule", "content": "Bob's now."}),
        ("retire", None),
        ("restore", None),
    ]:
        response = bob.post(f"{ITEMS}/{item['id']}/{path}", body)
        assert response.status_code == 404, (path, response.text)
    assert bob.get(f"{ITEMS}/{item['id']}/versions").status_code == 404
    assert alice.post(f"{ITEMS}/{uuid.uuid7()}/retire").status_code == 404
    assert alice.post(f"{ITEMS}/not-an-id/restore").status_code == 404
    assert counts(database) == before
    assert read(alice, item["id"])["status"] == "active"


def test_the_versions_of_an_entry_are_listed_oldest_first(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    first = write(alice, profile(content="One."))
    second = edit(alice, first["id"], {"kind": "profile", "content": "Two."})
    third = edit(alice, second["id"], {"kind": "profile", "content": "Three."})
    act(alice, third["id"], "retire")
    # Another entry, which is not one of them.
    write(alice, profile(content="Unrelated."))

    for asked in [first, second, third]:
        response = alice.get(f"{ITEMS}/{asked['id']}/versions")
        assert response.status_code == 200, response.text
        versions = response.json()
        assert [v["id"] for v in versions] == [first["id"], second["id"], third["id"]]
        assert [v["content"] for v in versions] == ["One.", "Two.", "Three."]
        assert [v["inactive_reason"] for v in versions] == [
            "superseded",
            "superseded",
            "retired",
        ]
    # An entry never edited has one version, itself.
    single = write(alice, rule())
    response = alice.get(f"{ITEMS}/{single['id']}/versions")
    assert [v["id"] for v in response.json()] == [single["id"]]
    assert alice.get(f"{ITEMS}/{uuid.uuid7()}/versions").status_code == 404


def test_nothing_is_removed_by_any_of_it(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    assert alice.post(TOPICS, {"name": "Rust"}).status_code == 201
    old = write(alice, profile(content="Prefers tabs.", topics=["rust"]))

    def rows() -> dict[str, set[tuple[Any, ...]]]:
        found = {}
        with admin_connection(database) as conn:
            for table, key in [
                ("items", "id, content"),
                ("item_subjects", "item_id, topic_id, entity_id"),
                ("item_relations", "id"),
                ("item_evidence", "id"),
                ("events", "id"),
            ]:
                found[table] = set(
                    conn.execute(f"SELECT {key} FROM {table}").fetchall()
                )
        return found

    seen = rows()
    new = edit(alice, old["id"], {"kind": "profile", "content": "Prefers spaces."})
    after_edit = rows()
    act(alice, new["id"], "retire")
    after_retire = rows()
    act(alice, new["id"], "restore")
    after_restore = rows()

    for later in [after_edit, after_retire, after_restore]:
        for table, found in seen.items():
            assert found <= later[table], table
        seen = later
    # Two versions, the relation, and the five events behind them all.
    assert counts(database) == {
        "items": 2,
        "item_subjects": 2,
        "item_relations": 1,
        "item_evidence": 5,
        "events": 5,
    }


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

    # The last step of each fails: for the edit, after the old version is
    # superseded and the new one, its subjects, its events and its evidence are
    # in; for retiring, after the status and the event.
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
    monkeypatch.setattr(items, "_link_judgment", fails)
    with pytest.raises(Boom):
        async with db.transaction(pool, ALICE) as conn:
            await items.retire(conn, item_id, now=now)

    assert counts(database) == before
    with admin_connection(database) as admin:
        assert admin.execute(
            "SELECT status, inactive_reason, content FROM items WHERE id = %s",
            (item_id,),
        ).fetchone() == ("active", None, "Prefers tabs.")
        # The named thing the failed edit would have made is not there either.
        assert admin.execute("SELECT count(*) FROM entities").fetchone() == (1,)


async def test_a_failed_restore_leaves_the_item_retired(
    pool: AsyncConnectionPool, database: str, monkeypatch: pytest.MonkeyPatch
) -> None:
    now = datetime.now(UTC)
    new = items.NewItem(
        kind="rule",
        content="Concise prose.",
        category=None,
        topics=(),
        entities=(),
        occurred_at=None,
        occurred_precision="instant",
    )
    async with db.transaction(pool, ALICE) as conn:
        item_id = await items.write(conn, new, now=now)
        await items.retire(conn, item_id, now=now)
    before = counts(database)

    class Boom(Exception):
        pass

    async def fails(*args: object, **kwargs: object) -> None:
        raise Boom

    monkeypatch.setattr(items, "_link_judgment", fails)
    with pytest.raises(Boom):
        async with db.transaction(pool, ALICE) as conn:
            await items.restore(conn, item_id, now=now)

    assert counts(database) == before
    with admin_connection(database) as admin:
        assert admin.execute(
            "SELECT status, inactive_reason FROM items WHERE id = %s", (item_id,)
        ).fetchone() == ("inactive", "retired")
