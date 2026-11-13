"""Who is asking the brain: `GET /brain/api/whoami`, through `platform-auth`,
and the `Origin` check in front of the whole app. The rules are in "How a
feature authenticates" in `High-level/docs/feature-contract.md`; the library
tests them in depth, and these check the brain gets them. The contract token
and the pinned clock are in conftest.py.
"""

import base64
import hashlib
import hmac
import json
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest
from fastapi.testclient import TestClient
from platform_auth.keys import KeyCache

from brain.app import create_app
from brain.settings import load
from tests.conftest import CLAIMS, JWKS, PLATFORM, TOKEN, bearer, contract_client
from tests.harness import app_url

WHOAMI = "/brain/api/whoami"


def b64(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def unb64(text: str) -> bytes:
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


def segment(value: dict[str, Any]) -> str:
    return b64(json.dumps(value).encode())


HEADER, PAYLOAD, SIGNATURE = TOKEN.split(".")
PUBLIC_KEY = unb64(JWKS["keys"][0]["x"])


def with_header(**changes: object) -> str:
    """The contract token's payload and signature under a header changed by
    `changes`, so the change is the one thing wrong with it."""
    header = {**json.loads(unb64(HEADER)), **changes}
    return f"{segment(header)}.{PAYLOAD}.{SIGNATURE}"


def bad_signature() -> str:
    flipped = bytearray(unb64(SIGNATURE))
    flipped[0] ^= 1
    return f"{HEADER}.{PAYLOAD}.{b64(bytes(flipped))}"


def alg_none() -> str:
    header = {**json.loads(unb64(HEADER)), "alg": "none"}
    return f"{segment(header)}.{PAYLOAD}."


def hs256_keyed_with(secret: bytes) -> str:
    header = {**json.loads(unb64(HEADER)), "alg": "HS256"}
    signing_input = f"{segment(header)}.{PAYLOAD}"
    mac = hmac.new(secret, signing_input.encode(), hashlib.sha256).digest()
    return f"{signing_input}.{b64(mac)}"


def client() -> TestClient:
    return contract_client()


def test_whoami_returns_the_account_in_the_token(at: Callable[[int], None]) -> None:
    at(CLAIMS["iat"])

    by_header = client().get(WHOAMI, headers=bearer(TOKEN))
    by_cookie = client().get(WHOAMI, headers={"cookie": f"__Host-access={TOKEN}"})

    assert by_header.status_code == 200
    assert by_header.json() == {"account_id": CLAIMS["sub"]}
    assert by_cookie.json() == {"account_id": CLAIMS["sub"]}


# Each way the brain must refuse, as a function from the contract to the
# request's headers and the time it is asked at.
REFUSED: dict[str, Callable[[], tuple[dict[str, str], int]]] = {
    "no token": lambda: ({}, CLAIMS["iat"]),
    "expired": lambda: (bearer(TOKEN), CLAIMS["exp"] + 6),
    "bad signature": lambda: (bearer(bad_signature()), CLAIMS["iat"]),
    "unknown key id": lambda: (bearer(with_header(kid="unknown")), CLAIMS["iat"]),
    "alg none": lambda: (bearer(alg_none()), CLAIMS["iat"]),
    "hs256 keyed with the public key": lambda: (
        bearer(hs256_keyed_with(PUBLIC_KEY)),
        CLAIMS["iat"],
    ),
    "hs256 keyed with the public key as in the key set": lambda: (
        bearer(hs256_keyed_with(JWKS["keys"][0]["x"].encode())),
        CLAIMS["iat"],
    ),
}


@pytest.mark.parametrize("case", REFUSED.values(), ids=REFUSED)
def test_whoami_without_a_valid_token_is_401(
    case: Callable[[], tuple[dict[str, str], int]], at: Callable[[int], None]
) -> None:
    headers, now = case()
    at(now)

    response = client().get(WHOAMI, headers=headers)

    assert response.status_code == 401
    assert response.content == b""


def test_the_contract_token_is_accepted_up_to_exp_plus_five(
    at: Callable[[int], None],
) -> None:
    # The control for the expired case above: the same token, five seconds
    # past its exp, is still the account's.
    at(CLAIMS["exp"] + 5)

    assert client().get(WHOAMI, headers=bearer(TOKEN)).status_code == 200


@pytest.mark.parametrize(
    "origins",
    [["https://evil.test"], [], ["https://platform.test.evil.test"]],
    ids=["another origin", "no origin", "a prefix match"],
)
def test_a_state_changing_request_needs_the_platforms_origin(
    origins: list[str], at: Callable[[int], None]
) -> None:
    at(CLAIMS["iat"])
    headers = [("origin", origin) for origin in origins] + list(bearer(TOKEN).items())

    # In front of routing, so even a route that takes no POST refuses first.
    refused = client().post(WHOAMI, headers=headers)
    assert refused.status_code == 403

    # The control: from the platform's own origin, the request reaches
    # routing, which has no POST here.
    allowed = client().post(WHOAMI, headers={"origin": PLATFORM, **bearer(TOKEN)})
    assert allowed.status_code == 405


def test_the_app_creates_and_closes_its_own_key_cache(
    web_dir: Path, database: str
) -> None:
    # Starting the app opens its database pool too, so it needs one to reach.
    app = create_app(
        load(
            {"BRAIN_WEB_DIR": str(web_dir), "BRAIN_APP_DATABASE_URL": app_url(database)}
        )
    )

    with TestClient(app):
        assert isinstance(app.state.keys, KeyCache)
        http = app.state.http
        assert not http.is_closed

    assert http.is_closed
