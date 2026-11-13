"""`brain purge <account-id> <item-id>`, against a real Postgres: memory made
through the running brain as brain_app, and purged as brain_owner, the role
the command connects as, on the test's own database. What it removes is
"Purging" in `Brain/README.md`, and why it exists at all is "Nothing is
destroyed, and only the owner retires" in `Brain/docs/trust-model.md`."""

import io
import os
import subprocess
import uuid
from collections.abc import Callable
from typing import Any

import psycopg
import pytest

from brain import db, purge
from tests.conftest import Caller
from tests.harness import admin_connection, app_url, owner_connection
from tests.test_corrections import act, edit
from tests.test_events import event, record
from tests.test_items import ALICE, BOB, profile, write
from tests.test_migrate import BRAIN_COMMAND, owner_url
from tests.test_now_and_history import decision
from tests.test_search import ids, search, supporting

ITEMS = "/brain/api/items"
TOPICS = "/brain/api/topics"

# Every row of the five tables a purge may touch, and the two it never does,
# by what names it: an id, or for item_subjects, which has none, the item
# and its subject.
QUERIES = {
    "items": "SELECT id::text FROM items",
    "item_subjects": (
        "SELECT item_id::text || '/' || coalesce(topic_id, entity_id)::text"
        " FROM item_subjects"
    ),
    "item_relations": "SELECT id::text FROM item_relations",
    "item_evidence": "SELECT id::text FROM item_evidence",
    "events": "SELECT id::text FROM events",
    "topics": "SELECT id::text FROM topics",
    "entities": "SELECT id::text FROM entities",
}


def everything(database: str) -> dict[str, set[str]]:
    """Every row, as the superuser sees them, past row level security."""
    with admin_connection(database) as conn:
        return {
            table: {str(row[0]) for row in conn.execute(query)}
            for table, query in QUERIES.items()
        }


def removed(
    before: dict[str, set[str]], after: dict[str, set[str]]
) -> dict[str, set[str]]:
    gone = {table: before[table] - after[table] for table in before}
    assert all(after[table] <= before[table] for table in before), "rows were added"
    return {table: rows for table, rows in gone.items() if rows}


def one(database: str, query: str, *args: object) -> set[str]:
    with admin_connection(database) as conn:
        return {str(row[0]) for row in conn.execute(query, args)}


def purging(
    database: str, account: uuid.UUID, item_id: str, answer: str = "yes\n"
) -> tuple[int, str, str]:
    """The command, as the owning role, `answer` on its stdin: its exit code,
    what it printed, and what it printed as errors."""
    out, err = io.StringIO(), io.StringIO()
    with owner_connection(database) as conn:
        code = purge.run(
            conn,
            account,
            uuid.UUID(item_id),
            stdin=io.StringIO(answer),
            stdout=out,
            stderr=err,
        )
    return code, out.getvalue(), err.getvalue()


def topic(caller: Caller, slug: str) -> None:
    response = caller.post(TOPICS, {"name": slug.title(), "slug": slug})
    assert response.status_code == 201, response.text


def versions(database: str, item_id: str) -> set[str]:
    return one(
        database,
        "SELECT id::text FROM items WHERE lineage_id"
        " = (SELECT lineage_id FROM items WHERE id = %s)",
        item_id,
    )


def subjects_of(database: str, item_ids: set[str]) -> set[str]:
    return one(
        database,
        "SELECT item_id::text || '/' || coalesce(topic_id, entity_id)::text"
        " FROM item_subjects WHERE item_id::text = ANY(%s)",
        list(item_ids),
    )


def relations_of(database: str, item_ids: set[str]) -> set[str]:
    return one(
        database,
        "SELECT id::text FROM item_relations"
        " WHERE from_item::text = ANY(%s) OR to_item::text = ANY(%s)",
        list(item_ids),
        list(item_ids),
    )


def evidence_of(database: str, item_ids: set[str]) -> set[str]:
    return one(
        database,
        "SELECT id::text FROM item_evidence WHERE item_id::text = ANY(%s)",
        list(item_ids),
    )


def events_of(database: str, item_ids: set[str]) -> set[str]:
    """Every event linked to the items: by subject_item, by item_evidence,
    and as the event that made one."""
    return one(
        database,
        "SELECT id::text FROM events WHERE subject_item::text = ANY(%s)"
        " UNION SELECT event_id::text FROM item_evidence WHERE item_id::text = ANY(%s)"
        " UNION SELECT created_by_event::text FROM items WHERE id::text = ANY(%s)",
        list(item_ids),
        list(item_ids),
        list(item_ids),
    )


def made_by(database: str, item_id: str) -> str:
    (found,) = one(
        database, "SELECT created_by_event::text FROM items WHERE id = %s", item_id
    )
    return found


def entry_with_history(alice: Caller) -> dict[str, Any]:
    """An entry about a topic and a named thing, edited once, the new version
    retired, with an event a feature sent about the first version: every
    kind of row a purge removes."""
    topic(alice, "rust")
    first = write(
        alice,
        profile(
            content="My bank password is hunter2-oops, pasted by mistake.",
            topics=["rust"],
            entities=["Personal Platform"],
        ),
    )
    second = edit(
        alice,
        first["id"],
        {"kind": "profile", "content": "My bank password is hunter3."},
    )
    act(alice, second["id"], "retire")
    record(alice, event(topic=..., item=first["id"], evidence="Opened hunter2-oops."))
    return first


def test_purge_removes_every_version_of_the_entry_and_what_hangs_on_it(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    first = entry_with_history(alice)
    other = write(alice, profile(content="Prefers short answers."))
    lineage = versions(database, first["id"])
    assert len(lineage) == 2
    expected = {
        "items": lineage,
        "item_subjects": subjects_of(database, lineage),
        "item_relations": relations_of(database, lineage),
        "item_evidence": evidence_of(database, lineage),
        "events": events_of(database, lineage),
    }
    # Two subjects on each version, the supersedes relation, the asserted
    # event of each version, the edit, the retirement and the feature's.
    assert [len(expected[t]) for t in expected] == [2, 4, 1, 4, 5]
    before = everything(database)

    # Asked for by its second version, it is still the whole entry.
    code, out, err = purging(database, ALICE, max(lineage))

    assert (code, err) == (0, "")
    assert removed(before, everything(database)) == expected
    # The other entry, the topic and the named thing are all still there.
    assert other["id"] in everything(database)["items"]
    assert "removed 16 rows" in out


def test_purge_keeps_an_event_that_still_supports_another_item(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    purged = write(alice, profile(content="Pasted a secret here."))
    other = write(alice, profile(content="Prefers short answers."))
    # The event that made the purged entry also supports the other one, as
    # consolidation may link it.
    shared = made_by(database, purged["id"])
    supporting(database, ALICE, other["id"], shared)
    # A feature's event about the purged entry that also supports the other:
    # kept, and no longer about an entry that is gone.
    about = record(alice, event(topic=..., item=purged["id"]))
    supporting(database, ALICE, other["id"], about["id"])

    code, out, _ = purging(database, ALICE, purged["id"])

    assert code == 0
    after = everything(database)
    assert {shared, about["id"]} <= after["events"]
    assert purged["id"] not in after["items"]
    assert len(evidence_of(database, {other["id"]})) == 3
    assert one(
        database, "SELECT subject_item::text FROM events WHERE id = %s", about["id"]
    ) == {"None"}
    assert about["id"] in out.split("Type yes")[0]


def test_purge_of_a_newer_decision_keeps_the_judgment_against_the_older(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    older = write(alice, decision(content="Postgres for everything."))
    newer = write(alice, decision(content="Postgres, and a secret by mistake."))
    response = alice.post(
        f"{ITEMS}/{older['id']}/replace", {"replaced_by": newer["id"]}
    )
    assert response.status_code == 200, response.text
    judgment = one(
        database, "SELECT id::text FROM events WHERE subject_item = %s", older["id"]
    )

    code, _, _ = purging(database, ALICE, newer["id"])

    assert code == 0
    after = everything(database)
    assert older["id"] in after["items"] and newer["id"] not in after["items"]
    # The judgment is evidence against the older decision, so it stays; the
    # relation that named the newer one is gone with it.
    assert judgment <= after["events"]
    assert relations_of(database, {older["id"]}) == set()


def test_purge_shows_what_it_will_remove_and_removes_nothing_without_yes(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    long = "A secret pasted by mistake, " + "and then a great deal more text " * 5
    item = write(alice, profile(content=long, topics=[], entities=["Bank"]))
    made = made_by(database, item["id"])
    (evidence,) = evidence_of(database, {item["id"]})
    (subject,) = subjects_of(database, {item["id"]})
    before = everything(database)

    for answer in ["", "\n", "no\n", "YES\n", "yes please\n", " yes\n", "y\n"]:
        code, out, _ = purging(database, ALICE, item["id"], answer)
        assert code == 1, answer
        assert everything(database) == before, answer
        assert "nothing was removed" in out, answer

    listing, prompt = out.split("Type yes")
    lines = listing.splitlines()
    # Each row by its table and id, with the first 80 characters of its text.
    assert f"{'items':<16}{item['id']}  {long[:80]}" in lines
    made_line = [line for line in lines if line.startswith(f"{'events':<16}{made}  ")]
    assert [len(line) for line in made_line] == [16 + 36 + 2 + 80]
    assert any(line.startswith(f"{'item_evidence':<16}{evidence}  ") for line in lines)
    assert f"{'item_subjects':<16}{subject}  named thing Bank" in lines
    assert prompt.startswith(" to remove these 4 rows for good:")


def test_purge_is_all_or_nothing(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    first = entry_with_history(alice)
    before = everything(database)
    # Events are removed last, so this fails after every other table's rows
    # were deleted in the same transaction.
    with admin_connection(database) as conn:
        conn.execute(
            "CREATE FUNCTION refuse() RETURNS trigger LANGUAGE plpgsql"
            " AS $$ BEGIN RAISE EXCEPTION 'refused for the test'; END $$"
        )
        conn.execute(
            "CREATE TRIGGER refuse BEFORE DELETE ON events"
            " FOR EACH ROW EXECUTE FUNCTION refuse()"
        )

    code, out, err = purging(database, ALICE, first["id"])

    assert code == 1
    assert "refused for the test" in err and "nothing was removed" in err
    assert "removed 16 rows" not in out
    assert everything(database) == before


def test_purge_of_an_item_the_account_does_not_have_says_so_and_exits_non_zero(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    bobs = write(calling(BOB), profile(content="Bob's own entry."))
    before = everything(database)
    unknown = str(uuid.uuid7())

    answers = {}
    for item_id in [unknown, bobs["id"]]:
        code, out, err = purging(database, ALICE, item_id)
        assert (code, out) == (1, "")
        answers[item_id] = err.replace(item_id, "<id>")

    assert answers[unknown] == answers[bobs["id"]]
    assert answers[unknown] == (
        f"there is no item <id> in the memory of {ALICE}, nothing was removed\n"
    )
    assert everything(database) == before


def test_purge_cannot_reach_another_accounts_rows(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice, bob = calling(ALICE), calling(BOB)
    alices = write(alice, profile(content="Alice's entry."))
    write(bob, profile(content="Bob's entry."))
    bobs_before = one(database, "SELECT id::text FROM items WHERE user_id = %s", BOB)

    # The owning role does not bypass row level security, which is forced,
    # so the account the command is given is all it can see.
    with admin_connection(database) as conn:
        (bypass,) = conn.execute(
            "SELECT rolbypassrls FROM pg_roles WHERE rolname = 'brain_owner'"
        ).fetchone() or (True,)
    assert bypass is False
    with owner_connection(database) as conn, conn.transaction(force_rollback=True):
        conn.execute("SELECT set_config(%s, %s, true)", (db.USER_SETTING, str(ALICE)))
        seen = {str(row[0]) for row in conn.execute("SELECT user_id FROM items")}
        deleted = conn.execute("DELETE FROM item_evidence WHERE user_id = %s", (BOB,))
        assert deleted.rowcount == 0
    assert seen == {str(ALICE)}

    code, _, _ = purging(database, ALICE, alices["id"])

    assert code == 0
    assert (
        one(database, "SELECT id::text FROM items WHERE user_id = %s", BOB)
        == bobs_before
    )
    assert one(database, "SELECT count(*) FROM events WHERE user_id = %s", BOB) == {"1"}


def test_the_service_still_cannot_delete(database: str) -> None:
    with admin_connection(database) as conn:
        tables = [
            str(row[0])
            for row in conn.execute(
                "SELECT tablename::text FROM pg_tables WHERE schemaname = 'public'"
            )
        ]
    assert {"items", "events", "item_evidence", "schema_migrations"} <= set(tables)
    with psycopg.connect(app_url(database), autocommit=True) as conn:
        for table in tables:
            for statement in [f"DELETE FROM {table}", f"TRUNCATE {table}"]:
                with pytest.raises(psycopg.errors.InsufficientPrivilege):
                    conn.execute(statement.encode())


def test_search_no_longer_finds_what_was_purged(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    secret = write(alice, profile(content="My bank password is hunter2."))
    kept = write(alice, profile(content="Hunter2 is also a film I liked."))
    assert ids(search(alice, q="password")) == [secret["id"]]

    code, _, _ = purging(database, ALICE, secret["id"])

    assert code == 0
    assert ids(search(alice, q="password")) == []
    assert ids(search(alice, q="hunter2")) == [kept["id"]]


def command(
    args: list[str], environ: dict[str, str], answer: str = ""
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(BRAIN_COMMAND), "purge", *args],
        env=environ,
        input=answer,
        capture_output=True,
        text=True,
        check=False,
    )


def test_the_command_purges_as_the_owner_from_a_terminal(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    item = write(calling(ALICE), profile(content="Pasted a secret here."))
    owner = {**os.environ, "BRAIN_OWNER_DATABASE_URL": owner_url(database)}
    unset = {k: v for k, v in os.environ.items() if k != "BRAIN_OWNER_DATABASE_URL"}

    assert command([str(ALICE), item["id"]], unset, "yes\n").returncode == 1
    assert command(["alice", item["id"]], owner, "yes\n").returncode == 2
    assert command([str(ALICE), "an-item"], owner, "yes\n").returncode == 2
    assert item["id"] in everything(database)["items"]

    done = command([str(ALICE), item["id"]], owner, "yes\n")

    assert done.returncode == 0, done.stderr
    assert "removed 3 rows for good" in done.stdout
    assert item["id"] not in everything(database)["items"]
