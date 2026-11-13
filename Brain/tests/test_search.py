"""Searching memory by its words, `GET /brain/api/search`, through the API on
a database of the test's own, as brain_app. Postgres' own text search, in
the `english` configuration, over what an item says and the evidence behind
it; never by meaning, and never ranked. Why there is no relevance score is
"What we are not doing" in `Brain/docs/brain-architecture.md`; the columns
it reads are in "The tables" in `Brain/docs/data-model.md`."""

import html
import uuid
from collections.abc import Callable
from datetime import UTC, datetime
from typing import Any
from urllib.parse import urlencode

from tests.conftest import Caller
from tests.harness import admin_connection
from tests.test_ask import ITEM_FIELDS
from tests.test_corrections import act, edit
from tests.test_events import event, record
from tests.test_items import ALICE, BOB, profile, rule, write
from tests.test_now_and_history import decision, state

SEARCH = "/brain/api/search"


def searching(caller: Caller, **query: Any) -> Any:
    """The response to a search, `query` as its query string, a list given
    as the name repeated."""
    return caller.get(f"{SEARCH}?{urlencode(query, doseq=True)}")


def search(caller: Caller, **query: Any) -> list[dict[str, Any]]:
    response = searching(caller, **query)
    assert response.status_code == 200, response.text
    found: list[dict[str, Any]] = response.json()
    return found


def ids(found: list[dict[str, Any]]) -> list[str]:
    return [each["id"] for each in found]


def supporting(database: str, user: uuid.UUID, item_id: str, event_id: str) -> None:
    """Links an event to an item as supporting evidence, as the superuser:
    what consolidation will do, and nothing the API does yet with an event a
    feature sent."""
    with admin_connection(database) as conn:
        conn.execute(
            "INSERT INTO item_evidence (id, user_id, item_id, event_id, kind,"
            " created_at) VALUES (%s, %s, %s, %s, 'supports', %s)",
            (uuid.uuid7(), user, item_id, event_id, datetime.now(UTC)),
        )


def test_search_finds_an_item_by_a_word_in_its_content(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    wanted = write(alice, profile(content="Enjoys sailing on weekends."))
    write(alice, profile(content="Prefers short answers."))

    assert ids(search(alice, q="sailing")) == [wanted["id"]]
    # Without letter case.
    assert ids(search(alice, q="SAILING")) == [wanted["id"]]


def test_search_finds_an_item_by_a_word_in_its_evidence(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    wanted = write(alice, state(content="Tuning the build."))
    write(alice, state(content="Writing the thesis."))
    seen = record(
        alice,
        event(verb="worked", evidence="Profiled the zeppelin pipeline for an hour."),
    )
    # The word is in the event alone, so nothing matches yet.
    assert search(alice, q="zeppelin") == []

    supporting(database, ALICE, wanted["id"], seen["id"])

    found = search(alice, q="zeppelin")
    assert ids(found) == [wanted["id"]]
    # The excerpt is from where the word is: the evidence.
    assert "<mark>zeppelin</mark>" in found[0]["excerpt"]


def test_search_finds_a_decision_by_a_word_in_its_reasoning(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    wanted = write(
        alice,
        decision(
            content="The brain is its own service.",
            details={
                "reasoning": "Splitting memory across languages was brittle.",
                "rejected": ["A module inside each feature"],
            },
        ),
    )
    write(
        alice,
        decision(
            content="Postgres holds every store.",
            details={"reasoning": "One database to run.", "rejected": []},
        ),
    )

    found = search(alice, q="brittle")
    assert ids(found) == [wanted["id"]]
    assert "<mark>brittle</mark>" in found[0]["excerpt"]
    # Every text value of details, the rejected options included.
    assert ids(search(alice, q="module")) == [wanted["id"]]


def test_search_matches_word_forms(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    wanted = write(alice, state(content="Reading how Postgres indexes work."))

    assert ids(search(alice, q="indexing")) == [wanted["id"]]
    assert ids(search(alice, q="indexed")) == [wanted["id"]]


def test_search_needs_every_word(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    both = write(alice, state(content="Learning Rust ownership rules."))
    write(alice, state(content="Learning Rust macros."))
    write(alice, state(content="Explaining Python ownership."))

    assert ids(search(alice, q="rust ownership")) == [both["id"]]


def test_search_does_not_match_inside_other_words_or_on_nothing(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    write(alice, state(content="Sorting notes into a category."))

    assert search(alice, q="cat") == []
    for blank in ["", "   ", "\t\n"]:
        assert searching(alice, q=blank).status_code == 422, repr(blank)
    assert alice.get(SEARCH).status_code == 422
    # 1 to 200 characters, once surrounding whitespace is dropped.
    assert searching(alice, q="a" * 200).status_code == 200
    assert searching(alice, q="a" * 201).status_code == 422
    assert searching(alice, q=f"  {'a' * 200}  ").status_code == 200


def test_search_can_be_limited_by_kind_and_to_active_items(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    a_rule = write(alice, rule(content="Answer about gardening briefly."))
    a_state = write(alice, state(content="Planning the gardening season."))
    a_profile = write(alice, profile(content="Loves gardening."))
    retired = write(alice, state(content="Gardening on the balcony."))
    act(alice, retired["id"], "retire")
    old = write(alice, profile(content="Gardening twice a week."))
    new = edit(alice, old["id"], {"kind": "profile", "content": "Gardening daily."})

    active = {a_rule["id"], a_state["id"], a_profile["id"], new["id"]}
    assert set(ids(search(alice, q="gardening"))) == active
    assert set(ids(search(alice, q="gardening", include_inactive="false"))) == active
    assert set(ids(search(alice, q="gardening", include_inactive="true"))) == active | {
        retired["id"],
        old["id"],
    }
    # kind repeated: any of them.
    assert set(ids(search(alice, q="gardening", kind=["rule", "state"]))) == {
        a_rule["id"],
        a_state["id"],
    }
    assert set(
        ids(search(alice, q="gardening", kind="state", include_inactive="true"))
    ) == {a_state["id"], retired["id"]}
    found = search(alice, q="gardening", include_inactive="true")
    assert {each["status"] for each in found} == {"active", "inactive"}
    assert searching(alice, q="gardening", kind="nonsense").status_code == 422
    # A field it does not know is refused, so a misspelt one is not lost.
    assert searching(alice, q="gardening", include_inactiv="true").status_code == 422


def test_search_returns_the_shared_item_shape_and_a_marked_excerpt(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    wanted = write(
        alice,
        state(content='Testing <script>alert("x")</script> & the parser\'s escaping.'),
    )

    found = search(alice, q="escaping")
    assert ids(found) == [wanted["id"]]
    [item] = found
    # The item as every answer gives it, and the excerpt.
    assert set(item) == ITEM_FIELDS | {"excerpt"}
    assert item["content"] == wanted["content"]
    assert item["status"] == "active"
    assert item["support"]["supports"]["assertion"] == 1
    assert [reason["verb"] for reason in item["evidence"]] == ["asserted"]
    excerpt = item["excerpt"]
    assert "<mark>escaping</mark>" in excerpt
    # Every other character is the owner's, escaped, so the page shows it as
    # text: the only markup is the mark, and unescaped, what is left is a
    # piece of what the owner wrote, every character kept.
    assert "&lt;/script&gt;" in excerpt
    assert "&amp;" in excerpt
    assert "&quot;" in excerpt
    unmarked = excerpt.replace("<mark>", "").replace("</mark>", "")
    assert "<" not in unmarked and ">" not in unmarked
    assert html.unescape(unmarked) in wanted["content"]


def test_search_orders_by_last_supported_newest_first(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    oldest = write(
        alice,
        profile(content="Juggling, since school.", occurred_at="2020-01-01T00:00:00Z"),
    )
    middle = write(
        alice,
        profile(content="Juggling clubs.", occurred_at="2022-01-01T00:00:00Z"),
    )
    newest = write(
        alice,
        profile(content="Juggling at work.", occurred_at="2024-01-01T00:00:00Z"),
    )

    found = search(alice, q="juggling")
    assert ids(found) == [newest["id"], middle["id"], oldest["id"]]
    # No rank, score or weight of any name: the order is the only judgement.
    assert set(found[0]) == ITEM_FIELDS | {"excerpt"}

    # Confirming the oldest supports it now, so it comes first.
    act(alice, oldest["id"], "confirm")
    assert ids(search(alice, q="juggling")) == [
        oldest["id"],
        newest["id"],
        middle["id"],
    ]

    # The limit keeps the newest: 50 by default, 1 to 200.
    assert ids(search(alice, q="juggling", limit=2)) == [oldest["id"], newest["id"]]
    assert searching(alice, q="juggling", limit=200).status_code == 200
    for refused in [0, 201, "many"]:
        assert searching(alice, q="juggling", limit=refused).status_code == 422


def test_search_answers_50_by_default(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    for n in range(51):
        write(alice, rule(content=f"Rule {n} about kayaks."))

    assert len(search(alice, q="kayaks")) == 50
    assert len(search(alice, q="kayaks", limit=51)) == 51


def test_search_never_crosses_users(
    calling: Callable[[uuid.UUID], Caller], database: str
) -> None:
    alice = calling(ALICE)
    bob = calling(BOB)
    mine = write(alice, state(content="Restoring a walnut table."))
    write(bob, state(content="Walnut veneer is fragile."))
    bobs_item = write(bob, state(content="Fixing a chair."))
    bobs_event = record(bob, event(verb="worked", evidence="Sanded the marquetry."))
    supporting(database, BOB, bobs_item["id"], bobs_event["id"])

    assert ids(search(alice, q="walnut")) == [mine["id"]]
    assert search(alice, q="marquetry") == []
    assert search(alice, q="fragile", include_inactive="true") == []
    assert ids(search(bob, q="marquetry")) == [bobs_item["id"]]


def test_a_query_with_search_operators_is_treated_as_plain_words(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    both = write(alice, state(content="Porting the Rust parser to Python."))
    write(alice, state(content="Writing a Rust parser."))
    write(alice, state(content="A Python script."))
    write(alice, state(content="Meeting a rustacean."))

    # Each of these is the two words rust and python, both required.
    for query in [
        "rust & python",
        "rust | python",
        "rust or python",
        "rust OR python",
        "!rust python",
        "-python rust",
        "rust:* python",
        '"python rust"',
        "(rust) (python)",
        "rust <-> python",
        "rust:A python",
        "'rust' 'python'",
        "rust\\ python",
    ]:
        assert ids(search(alice, q=query)) == [both["id"]], query
    # No prefix matching: rust:* does not find the rustacean.
    assert {i["content"] for i in search(alice, q="rust:*")} == {
        "Porting the Rust parser to Python.",
        "Writing a Rust parser.",
    }
    # Operators alone, or words too common to search on, are no error and
    # find nothing.
    for nothing in ["&", "| !", "():*", '""', "-", "<->", "or", "the and of"]:
        assert search(alice, q=nothing) == [], nothing
