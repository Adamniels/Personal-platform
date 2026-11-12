"""Canary's display data, what it offers the front page, and the switch that
makes it hang or fail. What a feature exposes is in "What a feature exposes
for the front page" in `Core/docs/core-architecture.md`.

The data is a contract between canary and the core, two languages, so it is
pinned by `contracts/display/canary.json`: the real response must have the
fixture's keys, each with the same JSON type.
"""

import json
import platform
import uuid
from pathlib import Path

import jwt
import pytest
from test_auth import KID, NO_VALID_TOKEN, bearer, client, mint

FIXTURE = Path(__file__).resolve().parents[2] / "contracts/display/canary.json"

DISPLAY = "/canary/api/display"


def json_types(body: dict) -> dict[str, type]:
    # Python's types of the parsed values, so an integer and a float, or a
    # bool and an integer, count as different, as the core's type would.
    return {key: type(value) for key, value in body.items()}


def test_the_display_data_matches_the_committed_fixture_in_keys_and_types():
    fixture = json.loads(FIXTURE.read_text())

    response = client().get(DISPLAY, headers=bearer(mint()))

    assert response.status_code == 200
    assert json_types(response.json()) == json_types(fixture)


def test_display_data_describes_the_verified_token():
    account_id = str(uuid.uuid7())
    token = mint(account_id)

    body = client().get(DISPLAY, headers=bearer(token)).json()

    assert body == {
        "account_id": account_id,
        "token_expires_at": jwt.decode(token, options={"verify_signature": False})[
            "exp"
        ],
        "key_id": KID,
        "python_version": platform.python_version(),
    }


@pytest.mark.parametrize("headers", NO_VALID_TOKEN.values(), ids=NO_VALID_TOKEN)
def test_display_accepts_a_bearer_token_and_refuses_without_one(headers):
    assert client().get(DISPLAY, headers=bearer(mint())).status_code == 200

    refused = client().get(DISPLAY, headers=headers())

    assert refused.status_code == 401
    assert refused.content == b""
