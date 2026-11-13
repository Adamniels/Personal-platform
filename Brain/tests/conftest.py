"""What more than one of the brain's tests needs: the contract token and its
key set, a clock to pin, an app that verifies against those keys, and a
stand in for the frontend's build.

Every token is the contract token, `contracts/token/token.jwt`, or one made
from it, verified against the committed key set answered from memory, so the
brain verifies what the core actually produces. The contract token expires at
a fixed time, so the clock the library reads is pinned per test."""

import json
from collections.abc import Callable
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import httpx2
import platform_auth.auth
import pytest
from fastapi.testclient import TestClient
from platform_auth.keys import KeyCache

from brain.app import create_app
from brain.settings import Settings, load

CONTRACT = Path(__file__).resolve().parents[2] / "contracts" / "token"
TOKEN = (CONTRACT / "token.jwt").read_text()
CLAIMS: dict[str, Any] = json.loads((CONTRACT / "claims.json").read_text())
JWKS: dict[str, Any] = json.loads((CONTRACT / "jwks.json").read_text())

CORE_URL = "http://127.0.0.1:8080"
PLATFORM = "https://platform.test"


@pytest.fixture
def at(monkeypatch: pytest.MonkeyPatch) -> Callable[[int], None]:
    """Pins the clock the library reads once per request, `time.time()` in
    `platform_auth.auth`, to whole seconds since the epoch. Only that module's
    binding is replaced, so nothing else in the process sees the change."""

    def pin(now: int) -> None:
        monkeypatch.setattr(
            platform_auth.auth, "time", SimpleNamespace(time=lambda: float(now))
        )

    return pin


def serves_the_contract_keys(request: httpx2.Request) -> httpx2.Response:
    return httpx2.Response(200, json=JWKS)


def contract_client(settings: Settings | None = None) -> TestClient:
    """The app over a key cache answered from the committed key set, called
    as the platform's origin, without entering its lifespan."""
    keys = KeyCache(
        httpx2.AsyncClient(transport=httpx2.MockTransport(serves_the_contract_keys)),
        CORE_URL,
    )
    app = create_app(settings or load({}), keys=keys)
    return TestClient(app, base_url=PLATFORM)


def bearer(token: str) -> dict[str, str]:
    return {"authorization": f"Bearer {token}"}


INDEX = "<!doctype html><title>Memory</title><div id=root></div>\n"
ASSET = "console.log('the memory centre');\n"


@pytest.fixture
def web_dir(tmp_path: Path) -> Path:
    """A folder shaped like `npm run build`'s output in Brain/web: index.html
    and a hashed file under assets/. The brain's tests never depend on a real
    build, which the brain's CI job does not make."""
    (tmp_path / "assets").mkdir()
    (tmp_path / "index.html").write_text(INDEX)
    (tmp_path / "assets" / "index-abc123.js").write_text(ASSET)
    return tmp_path
