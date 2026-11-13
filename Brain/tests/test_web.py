"""The memory centre's frontend, served by the brain: `index.html` for the
application's addresses, behind the library's `page` wrapper, and the built
files under `/brain/assets/`. See "The memory centre" in Brain/README.md.

A folder shaped like the build stands in for it, from conftest.py."""

from collections.abc import Callable
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from brain.app import create_app
from brain.settings import load
from tests.conftest import ASSET, CLAIMS, INDEX, TOKEN, bearer, contract_client
from tests.harness import MINIMAL_ENV


def serving(web_dir: Path) -> TestClient:
    return contract_client(load({**MINIMAL_ENV, "BRAIN_WEB_DIR": str(web_dir)}))


def test_the_brain_serves_the_application(
    web_dir: Path, at: Callable[[int], None]
) -> None:
    at(CLAIMS["iat"])
    client = serving(web_dir)

    # Every section's address answers the same page; the application reads
    # the address and decides, an unknown section included.
    for path in ["/brain", "/brain/knowledge", "/brain/no-such-section"]:
        page = client.get(path, headers=bearer(TOKEN))
        assert page.status_code == 200, path
        assert page.text == INDEX, path
        assert page.headers["content-type"].startswith("text/html"), path
        assert page.headers["cache-control"] == "no-cache", path

    # Without a session, the core decides: refresh, or log in, and back.
    for path, back in [
        ("/brain", "%2Fbrain"),
        ("/brain/knowledge?q=a", "%2Fbrain%2Fknowledge%3Fq%3Da"),
    ]:
        refused = client.get(path, follow_redirects=False)
        assert refused.status_code == 303, path
        assert refused.headers["location"] == f"/auth/refresh?return_to={back}"

    # A built file, with or without a session: it holds code, never data.
    asset = client.get("/brain/assets/index-abc123.js")
    assert asset.status_code == 200
    assert asset.text == ASSET
    assert asset.headers["content-type"].startswith("text/javascript")
    assert client.get("/brain/assets/missing.js").status_code == 404

    # The API and the assets folder are never answered by the page, with a
    # session or without one, and nor is a path below a section.
    for path in [
        "/brain/api",
        "/brain/api/no-such-route",
        "/brain/assets",
        "/brain/knowledge/deeper",
    ]:
        for headers in [bearer(TOKEN), {}]:
            answer = client.get(path, headers=headers, follow_redirects=False)
            assert answer.status_code == 404, (path, headers)
            assert answer.text != INDEX, (path, headers)
    # With a trailing slash, FastAPI may first redirect to the path without
    # it, above; never to the core and never with the page.
    for path in ["/brain/api/", "/brain/assets/"]:
        for headers in [bearer(TOKEN), {}]:
            answer = client.get(path, headers=headers, follow_redirects=False)
            assert answer.status_code not in (200, 303), (path, headers)
            assert answer.text != INDEX, (path, headers)

    # The API is still the API.
    assert client.get("/brain/api/whoami", headers=bearer(TOKEN)).json() == {
        "account_id": CLAIMS["sub"]
    }
    assert client.get("/brain/api/whoami").status_code == 401


def test_a_missing_build_stops_the_brain_at_start(tmp_path: Path) -> None:
    for web_dir in [tmp_path / "absent", tmp_path]:
        app = create_app(load({**MINIMAL_ENV, "BRAIN_WEB_DIR": str(web_dir)}))

        with pytest.raises(RuntimeError, match="BRAIN_WEB_DIR"), TestClient(app):
            pass


def test_the_page_routes_stay_out_of_the_schema(web_dir: Path) -> None:
    # The schema is the brain's API for the frontend and features; a page is
    # not part of it.
    paths = create_app(load({**MINIMAL_ENV, "BRAIN_WEB_DIR": str(web_dir)})).openapi()[
        "paths"
    ]

    assert all(path.startswith("/brain/api/") for path in paths)
