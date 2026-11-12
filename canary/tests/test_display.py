"""Canary's display data, what it offers the front page, and the switch that
makes it hang or fail. What a feature exposes is in "What a feature exposes
for the front page" in `Core/docs/core-architecture.md`.

The data is a contract between canary and the core, two languages, so it is
pinned by `contracts/display/canary.json`: the real response must have the
fixture's keys, each with the same JSON type.
"""

import asyncio
import json
import os
import platform
import subprocess
import sys
import uuid
from pathlib import Path

import httpx2
import jwt
import pytest
from starlette.testclient import TestClient
from test_auth import (
    CORE_URL,
    KID,
    NO_VALID_TOKEN,
    bearer,
    client,
    mint,
    serves_the_key,
)

from canary.app import create_app
from canary.keys import KeyCache
from canary.settings import DisplayMode, SettingsError, load

FIXTURE = Path(__file__).resolve().parents[2] / "contracts/display/canary.json"

DISPLAY = "/canary/api/display"


def app_in(mode: str, **kwargs):
    keys = KeyCache(
        httpx2.AsyncClient(transport=httpx2.MockTransport(serves_the_key)), CORE_URL
    )
    return create_app(load({"CANARY_DISPLAY_MODE": mode}), keys=keys, **kwargs)


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


def test_error_mode_answers_500():
    in_error = TestClient(app_in("error"), base_url="https://platform.test")

    # Before authentication, the way a broken feature would: with a valid
    # token and without one alike.
    assert in_error.get(DISPLAY, headers=bearer(mint())).status_code == 500
    assert in_error.get(DISPLAY).status_code == 500
    # Only the display endpoint is switched.
    assert in_error.get("/canary/api/whoami", headers=bearer(mint())).status_code == 200


def test_hang_mode_does_not_answer():
    # The hang is far longer than the wait, and the wait's cancellation ends it.
    app = app_in("hang", hang_seconds=60)

    async def ask() -> None:
        transport = httpx2.ASGITransport(app=app)
        async with httpx2.AsyncClient(
            transport=transport, base_url="https://platform.test"
        ) as http:
            with pytest.raises(TimeoutError):
                await asyncio.wait_for(
                    http.get(DISPLAY, headers=bearer(mint())), timeout=0.5
                )
            # Only the display endpoint is switched.
            whoami = await http.get("/canary/api/whoami", headers=bearer(mint()))
            assert whoami.status_code == 200

    asyncio.run(ask())


def test_an_unknown_mode_refuses_to_start():
    assert load({}).display_mode is DisplayMode.NORMAL
    for mode in DisplayMode:
        assert load({"CANARY_DISPLAY_MODE": mode.value}).display_mode is mode
    for value in ["", "slow", "Hang", " error", "normal "]:
        with pytest.raises(SettingsError, match="CANARY_DISPLAY_MODE"):
            load({"CANARY_DISPLAY_MODE": value})

    environ = {**os.environ, "CANARY_DISPLAY_MODE": "slow"}
    started = subprocess.run(
        [sys.executable, "-c", "import canary.app"],
        check=False,
        env=environ,
        capture_output=True,
        text=True,
    )

    assert started.returncode != 0
    assert "SettingsError: CANARY_DISPLAY_MODE" in started.stderr
