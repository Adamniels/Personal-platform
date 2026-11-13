"""Named things, the brain's other kind of subject: a name, compared without
letter case, and an optional label. And both kinds of subject kept to the
user who made them. Through the API, on a database of the test's own, as
brain_app. The rules are in "Scope is topic and entity, never feature" in
`Brain/docs/trust-model.md`."""

import uuid
from collections.abc import Callable
from typing import Any

import pytest

from tests.conftest import Caller

ALICE = uuid.UUID("0190a8f0-0000-7000-8000-00000000a11c")
BOB = uuid.UUID("0190a8f0-0000-7000-8000-000000000b0b")

ENTITIES = "/brain/api/entities"
TOPICS = "/brain/api/topics"


def create(caller: Caller, body: object) -> dict[str, Any]:
    response = caller.post(ENTITIES, body)
    assert response.status_code == 201, response.text
    entity: dict[str, Any] = response.json()
    return entity


def listed(caller: Caller) -> list[dict[str, Any]]:
    response = caller.get(ENTITIES)
    assert response.status_code == 200, response.text
    entities: list[dict[str, Any]] = response.json()
    return entities


def test_a_named_thing_is_created_once_whatever_its_letter_case(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice = calling(ALICE)
    entity = create(alice, {"name": "Personal Platform", "label": "project"})

    assert entity["name"] == "Personal Platform"
    assert entity["label"] == "project"
    assert uuid.UUID(entity["id"]).version == 7
    for name in ["personal platform", "PERSONAL PLATFORM", "  Personal Platform "]:
        assert alice.post(ENTITIES, {"name": name}).status_code == 409, name
    assert listed(alice) == [entity]


def test_a_label_is_optional(calling: Callable[[uuid.UUID], Caller]) -> None:
    alice = calling(ALICE)

    entity = create(alice, {"name": "Operation Rollout"})

    assert entity["label"] is None
    assert listed(alice) == [entity]


@pytest.mark.parametrize(
    "body",
    [{}, {"name": ""}, {"name": "   "}, {"name": "A", "label": ""}, {"name": 1}],
    ids=["no name", "empty name", "blank name", "empty label", "name not text"],
)
def test_a_named_thing_needs_a_name(
    body: object, calling: Callable[[uuid.UUID], Caller]
) -> None:
    alice = calling(ALICE)

    assert alice.post(ENTITIES, body).status_code == 422
    assert listed(alice) == []


def test_topics_and_named_things_are_scoped_to_their_user(
    calling: Callable[[uuid.UUID], Caller],
) -> None:
    alice, bob = calling(ALICE), calling(BOB)
    topic = alice.post(TOPICS, {"name": "Postgres indexing"}).json()
    broader = alice.post(TOPICS, {"name": "Databases"}).json()
    relation = alice.post(
        f"{TOPICS}/{topic['id']}/broader", {"broader_id": broader["id"]}
    ).json()
    entity = create(alice, {"name": "Personal Platform", "label": "project"})

    # Bob sees none of it.
    assert bob.get(TOPICS).json() == []
    assert listed(bob) == []

    # Nor can he reach it by its id.
    for path, body in [
        (f"{TOPICS}/{topic['id']}/promote", None),
        (f"{TOPICS}/{topic['id']}/separate", None),
        (f"{TOPICS}/{topic['id']}/same-as", {"target_id": broader["id"]}),
        (f"{TOPICS}/{topic['id']}/broader", {"broader_id": broader["id"]}),
        (f"/brain/api/topic-relations/{relation['id']}/end", None),
    ]:
        assert bob.post(path, body).status_code == 404, path

    # Nor point at it from his own.
    own = bob.post(TOPICS, {"name": "Postgres indexing"})
    assert own.status_code == 201
    mine = own.json()
    assert (
        bob.post(
            f"{TOPICS}/{mine['id']}/same-as", {"target_id": topic["id"]}
        ).status_code
        == 422
    )
    assert (
        bob.post(
            f"{TOPICS}/{mine['id']}/broader", {"broader_id": broader["id"]}
        ).status_code
        == 422
    )

    # His names are his own, the same ones included.
    assert create(bob, {"name": "personal platform"})["name"] == "personal platform"

    # And Alice's are as she left them.
    assert alice.get(TOPICS).json() == [broader, topic]
    assert listed(alice) == [entity]
