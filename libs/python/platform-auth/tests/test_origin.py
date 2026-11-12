"""The Origin check, over a stub route that answers anything, as
`Core/tests/origin.rs` does with the same table: every method that can change
state needs the platform's own origin, and a GET is never checked."""

import pytest
from starlette.applications import Starlette
from starlette.middleware import Middleware
from starlette.requests import Request
from starlette.responses import PlainTextResponse
from starlette.routing import Route
from starlette.testclient import TestClient

from platform_auth.origin import OriginCheck

PLATFORM = "https://platform.test"
METHODS = ["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"]


async def reached(request: Request) -> PlainTextResponse:
    return PlainTextResponse("reached")


def app_at(path: str, origin: str) -> TestClient:
    """A service with one route at `path`, answering anything, behind the
    check for `origin`."""
    return TestClient(
        Starlette(
            routes=[Route(path, reached, methods=METHODS)],
            middleware=[Middleware(OriginCheck, origin=origin)],
        )
    )


stub = app_at("/", PLATFORM)

CASES = [
    ("POST", [PLATFORM], 200),
    ("POST", ["https://evil.test"], 403),
    ("POST", [], 403),
    ("POST", ["null"], 403),
    ("POST", ["http://platform.test"], 403),
    ("POST", ["https://platform.test:8443"], 403),
    ("POST", ["https://platform.test.evil.test"], 403),
    ("POST", ["https://platform.test/"], 403),
    ("POST", [PLATFORM, "https://evil.test"], 403),
    ("POST", [PLATFORM, PLATFORM], 403),
    ("PUT", [], 403),
    ("PATCH", ["https://evil.test"], 403),
    ("DELETE", [PLATFORM], 200),
    ("GET", ["https://evil.test"], 200),
    ("GET", [], 200),
    ("HEAD", ["https://evil.test"], 200),
    ("OPTIONS", ["https://evil.test"], 200),
]


@pytest.mark.parametrize(
    "method, origins, expected",
    CASES,
    ids=[f"{method} {origins}" for method, origins, _ in CASES],
)
def test_only_the_platforms_own_origin_may_change_state(
    method: str, origins: list[str], expected: int
) -> None:
    headers = [("origin", origin) for origin in origins]

    response = stub.request(method, "/", headers=headers)

    assert response.status_code == expected


def test_a_refusal_is_plain_and_never_reaches_the_route() -> None:
    response = stub.post("/", headers={"origin": "https://evil.test"})

    assert response.status_code == 403
    assert "reached" not in response.text


def test_the_check_sits_in_front_of_the_whole_app() -> None:
    # A path the service does not serve is still refused before routing, so a
    # route added later is covered without anyone remembering to.
    app = app_at("/feature", "https://other.test")

    assert app.post("/feature", headers={"origin": PLATFORM}).status_code == 403
    assert app.post("/nowhere", headers={"origin": PLATFORM}).status_code == 403
    assert (
        app.post("/nowhere", headers={"origin": "https://other.test"}).status_code
        == 404
    )
