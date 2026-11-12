"""Canary's edge: where the token is read from, and what a request without a
valid one gets. The rules are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`.

The app is built with its own key cache, over a key set answered from memory,
so these tests mint tokens with a key they hold and never reach a real core.
"""

import base64
import time
import uuid

import httpx2
import jwt
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from starlette.requests import Request
from starlette.testclient import TestClient

from canary.app import create_app, render_page
from canary.auth import bearer_token
from canary.keys import KeyCache
from canary.settings import load

KID = "canary-test"
CORE_URL = "http://127.0.0.1:8080"
SIGNING_KEY = Ed25519PrivateKey.generate()


def jwks(key: Ed25519PrivateKey, kid: str = KID) -> dict:
    raw = key.public_key().public_bytes(
        serialization.Encoding.Raw, serialization.PublicFormat.Raw
    )
    x = base64.urlsafe_b64encode(raw).rstrip(b"=").decode()
    return {
        "keys": [{"kty": "OKP", "crv": "Ed25519", "alg": "EdDSA", "kid": kid, "x": x}]
    }


def mint(
    account_id: str | None = None,
    expires_in: int = 1800,
    key: Ed25519PrivateKey = SIGNING_KEY,
) -> str:
    now = int(time.time())
    claims = {
        "sub": account_id or str(uuid.uuid7()),
        "sid": str(uuid.uuid7()),
        "jti": str(uuid.uuid7()),
        "iat": now,
        "exp": now + expires_in,
    }
    return jwt.encode(claims, key, algorithm="EdDSA", headers={"kid": KID})


def serves_the_key(request: httpx2.Request) -> httpx2.Response:
    return httpx2.Response(200, json=jwks(SIGNING_KEY))


def unreachable(request: httpx2.Request) -> httpx2.Response:
    raise httpx2.ConnectError("connection refused", request=request)


def client(core=serves_the_key) -> TestClient:
    keys = KeyCache(httpx2.AsyncClient(transport=httpx2.MockTransport(core)), CORE_URL)
    # Same origin as Caddy's, so a redirect's Location can be read as sent.
    return TestClient(
        create_app(load({}), keys=keys),
        base_url="https://platform.test",
        follow_redirects=False,
    )


def cookie(token: str) -> dict:
    return {"cookie": f"__Host-access={token}"}


def bearer(token: str) -> dict:
    return {"authorization": f"Bearer {token}"}


# The three ways a request has no valid token, which must look the same.
NO_VALID_TOKEN = {
    "none": dict,
    "expired": lambda: cookie(mint(expires_in=-3600)),
    "bad signature": lambda: cookie(mint(key=Ed25519PrivateKey.generate())),
}


def test_a_page_shows_the_account_from_a_cookie_token():
    account_id = str(uuid.uuid7())

    response = client().get("/canary", headers=cookie(mint(account_id)))

    assert response.status_code == 200
    assert response.headers["content-type"].startswith("text/html")
    assert account_id in response.text


def test_an_api_call_accepts_the_token_in_an_authorization_header():
    account_id = str(uuid.uuid7())

    response = client().get("/canary/api/whoami", headers=bearer(mint(account_id)))

    assert response.status_code == 200
    assert response.json() == {"account_id": account_id}


def test_an_api_call_accepts_the_token_in_the_cookie():
    account_id = str(uuid.uuid7())

    response = client().get("/canary/api/whoami", headers=cookie(mint(account_id)))

    assert response.json() == {"account_id": account_id}


@pytest.mark.parametrize("headers", NO_VALID_TOKEN.values(), ids=NO_VALID_TOKEN)
def test_a_page_without_a_valid_token_redirects_to_refresh_with_the_way_back(headers):
    response = client().get("/canary?a=1", headers=headers())

    assert response.status_code == 303
    assert response.headers["location"] == "/auth/refresh?return_to=%2Fcanary%3Fa%3D1"


def test_the_way_back_is_the_path_as_received():
    # Already encoded bytes are encoded once more, so the core decodes back
    # to exactly what the browser sent. Canary never judges the path itself.
    response = client().get("/canary?q=%2F%3Cx%3E&b=")

    assert response.headers["location"] == (
        "/auth/refresh?return_to=%2Fcanary%3Fq%3D%252F%253Cx%253E%26b%3D"
    )


@pytest.mark.parametrize("headers", NO_VALID_TOKEN.values(), ids=NO_VALID_TOKEN)
def test_an_api_call_without_a_valid_token_gets_401(headers):
    response = client().get("/canary/api/whoami", headers=headers())

    assert response.status_code == 401
    assert response.content == b""
    assert "location" not in response.headers


@pytest.mark.parametrize(
    "authorization",
    [
        "Bearer not-a-token",
        f"Bearer {mint(expires_in=-3600)}",
        f"Bearer {mint(key=Ed25519PrivateKey.generate())}",
        "Bearer ",
        f"Basic {mint()}",
        mint(),
    ],
    ids=["garbage", "expired", "bad signature", "empty", "basic", "no scheme"],
)
def test_an_invalid_header_is_not_rescued_by_a_valid_cookie(authorization):
    headers = {**cookie(mint()), "authorization": authorization}

    assert client().get("/canary/api/whoami", headers=headers).status_code == 401
    assert client().get("/canary", headers=headers).status_code == 303


def test_two_authorization_headers_are_refused():
    # Two valid headers, so only the count can refuse them. The request is
    # built from a raw scope first, with no client that could fold the two
    # into one value, which the space in it would refuse for another reason.
    token = mint()
    raw = [(b"authorization", f"Bearer {token}".encode())]
    assert bearer_token(Request({"type": "http", "headers": raw})) == token
    assert bearer_token(Request({"type": "http", "headers": raw * 2})) is None

    headers = [
        ("authorization", f"Bearer {token}"),
        ("authorization", f"Bearer {token}"),
    ]
    assert client().get("/canary/api/whoami", headers=headers).status_code == 401
    assert client().get("/canary", headers=headers).status_code == 303


def test_the_bearer_scheme_is_case_insensitive():
    headers = {"authorization": f"bearer {mint()}"}

    assert client().get("/canary/api/whoami", headers=headers).status_code == 200


def test_an_unreachable_core_with_no_cached_key_counts_as_no_valid_token():
    unreached = client(core=unreachable)
    token = mint()

    page = unreached.get("/canary", headers=cookie(token))
    api = unreached.get("/canary/api/whoami", headers=bearer(token))

    assert page.status_code == 303
    assert page.headers["location"] == "/auth/refresh?return_to=%2Fcanary"
    assert api.status_code == 401
    assert api.content == b""


def test_the_page_escapes_what_it_shows():
    # The account id is a UUID by the time it is shown, so nothing in it can
    # be markup; the page escapes anyway, so the next value shown is safe too.
    html = render_page('<script>alert("x")</script>')

    assert "<script>alert" not in html
    assert "&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;" in html


def test_the_page_carries_the_two_nav_lines_and_not_the_cores_stylesheet():
    text = client().get("/canary", headers=cookie(mint())).text

    assert '<script src="/nav.js" defer></script>' in text
    assert '<platform-nav current="canary"></platform-nav>' in text
    assert "core.css" not in text


def test_paths_canary_does_not_serve_answer_404():
    signed_in = cookie(mint())
    for path in ["/canary/anything", "/canary/api", "/canary/api/nothing", "/"]:
        assert client().get(path, headers=signed_in).status_code == 404, path


def test_the_app_creates_and_closes_its_own_key_cache():
    app = create_app(load({}))

    with TestClient(app):
        assert isinstance(app.state.keys, KeyCache)
        http = app.state.http
        assert not http.is_closed

    assert http.is_closed
