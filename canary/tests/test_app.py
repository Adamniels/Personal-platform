"""Canary's app: its page, and how it wires in `platform-auth`. What the
library itself does with a token is tested in `libs/python/platform-auth/`;
these tests check that canary still gets it, through its own routes.

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
from platform_auth.keys import KeyCache
from starlette.testclient import TestClient

from canary.app import create_app, render_page
from canary.settings import load

KID = "canary-test"
CORE_URL = "http://127.0.0.1:8080"
SIGNING_KEY = Ed25519PrivateKey.generate()
PLATFORM = "https://platform.test"


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


def client(core=serves_the_key) -> TestClient:
    keys = KeyCache(httpx2.AsyncClient(transport=httpx2.MockTransport(core)), CORE_URL)
    # Same origin as Caddy's, so a redirect's Location can be read as sent.
    return TestClient(
        create_app(load({}), keys=keys),
        base_url=PLATFORM,
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


@pytest.mark.parametrize("headers", NO_VALID_TOKEN.values(), ids=NO_VALID_TOKEN)
def test_canary_still_refuses_what_it_refused(headers):
    # What canary answered before the library, through its own routes: the
    # page sends the browser to refresh with the way back, the API says 401
    # with nothing in the body.
    page = client().get("/canary?a=1", headers=headers())
    api = client().get("/canary/api/whoami", headers=headers())

    assert page.status_code == 303
    assert page.headers["location"] == "/auth/refresh?return_to=%2Fcanary%3Fa%3D1"
    assert api.status_code == 401
    assert api.content == b""
    assert "location" not in api.headers


def test_a_page_shows_the_account_from_a_cookie_token():
    account_id = str(uuid.uuid7())

    response = client().get("/canary", headers=cookie(mint(account_id)))

    assert response.status_code == 200
    assert response.headers["content-type"].startswith("text/html")
    assert account_id in response.text


def test_whoami_answers_the_account_in_the_token():
    account_id = str(uuid.uuid7())

    response = client().get("/canary/api/whoami", headers=bearer(mint(account_id)))

    assert response.status_code == 200
    assert response.json() == {"account_id": account_id}


def test_the_page_escapes_what_it_shows():
    # The account id is a UUID by the time it is shown, so nothing in it can
    # be markup; the page escapes anyway, so the next value shown is safe too.
    html = render_page('<script>alert("x")</script>')

    assert "<script>alert" not in html
    assert "&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;" in html


def test_the_page_carries_the_nav_and_the_design_files_and_not_the_cores_stylesheet():
    text = client().get("/canary", headers=cookie(mint())).text

    for line in [
        '<link rel="stylesheet" href="/design/tokens.css">',
        '<link rel="stylesheet" href="/design/fonts.css">',
        '<link rel="stylesheet" href="/design/page.css">',
        '<script src="/design/theme.js"></script>',
        '<script src="/design/nav.js" defer></script>',
        '<platform-nav current="canary"></platform-nav>',
    ]:
        assert line in text, line
    # The theme is applied in the head, before the body is parsed.
    assert text.index("/design/theme.js") < text.index("<body>")
    assert 'src="/nav.js"' not in text
    assert "core.css" not in text
    # No style of its own: the page container comes from /design/page.css.
    assert "<style" not in text
    assert '<main class="page">' in text


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


def test_the_origin_check_sits_in_front_of_the_whole_app():
    # A path canary does not serve is still refused before routing, so a
    # route added later is covered without anyone remembering to. The check
    # itself is the library's; this is canary wiring it in with its setting.
    app = TestClient(create_app(load({"CANARY_PUBLIC_ORIGIN": "https://other.test"})))

    assert app.post("/canary", headers={"origin": PLATFORM}).status_code == 403
    assert app.post("/nowhere", headers={"origin": PLATFORM}).status_code == 403
    assert (
        app.post("/nowhere", headers={"origin": "https://other.test"}).status_code
        == 404
    )
