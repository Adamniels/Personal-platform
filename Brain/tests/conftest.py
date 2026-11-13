"""What more than one of the brain's tests needs.

The database fixtures, built from `tests/harness.py`, which says how the test
databases are made and which connection strings they need.

The contract token and its key set, a clock to pin, an app that verifies
against those keys, and a stand in for the frontend's build. Every token is
the contract token, `contracts/token/token.jwt`, or one made from it,
verified against the committed key set answered from memory, so the brain
verifies what the core actually produces. The contract token expires at a
fixed time, so the clock the library reads is pinned per test.

Tests that act as more than one user, through the running brain, mint their
own tokens instead, with `calling(user)`, see `mint` below."""

import base64
import json
import time
import uuid
from collections.abc import AsyncIterator, Callable, Generator, Iterator
from pathlib import Path
from types import SimpleNamespace
from typing import Any
from uuid import UUID

import httpx2
import jwt
import platform_auth.auth
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from fastapi.testclient import TestClient
from platform_auth.keys import KeyCache
from psycopg_pool import AsyncConnectionPool

from brain import migrate
from brain.app import create_app
from brain.settings import Settings, load
from tests.harness import (
    MIGRATIONS,
    MINIMAL_ENV,
    app_url,
    create_database,
    drop_database,
    owner_connection,
    sweep,
    unique_name,
)

_FAILED = pytest.StashKey[bool]()


@pytest.hookimpl(wrapper=True)
def pytest_runtest_makereport(
    item: pytest.Item, call: pytest.CallInfo[None]
) -> Generator[None, pytest.TestReport, pytest.TestReport]:
    # Kept on the item, so a fixture can tell at teardown whether its test
    # passed, and keep the database of one that did not. The call's report
    # is made before the fixtures are torn down.
    report = yield
    if report.failed:
        item.stash[_FAILED] = True
    return report


def _keep_or_drop(request: pytest.FixtureRequest, name: str) -> None:
    if request.node.stash.get(_FAILED, False):
        print(f"kept {name} for a look, a later run sweeps it")
    else:
        drop_database(name)


@pytest.fixture(scope="session")
def template() -> Iterator[str]:
    """Migrated once per session, from the files in Brain/migrations/."""
    sweep()
    name = unique_name("tpl")
    create_database(name)
    with owner_connection(name) as conn:
        migrate.run(conn, MIGRATIONS)
    # A template cannot be copied while anyone is connected to it, and the
    # connection above is closed by now.
    yield name
    drop_database(name)


@pytest.fixture
def database(template: str, request: pytest.FixtureRequest) -> Iterator[str]:
    """This test's own copy of the migrated template."""
    name = unique_name("test")
    create_database(name, template=template)
    yield name
    _keep_or_drop(request, name)


@pytest.fixture
def empty_database(request: pytest.FixtureRequest) -> Iterator[str]:
    """A database of its own, set up as brain.sh sets up `brain` and with no
    migration applied, for testing `brain migrate` itself."""
    sweep()
    name = unique_name("empty")
    create_database(name)
    yield name
    _keep_or_drop(request, name)


@pytest.fixture
async def pool(database: str) -> AsyncIterator[AsyncConnectionPool]:
    """A pool of one connection as brain_app, so a test knows the next
    checkout is the same connection the last one returned."""
    pool = AsyncConnectionPool(app_url(database), min_size=1, max_size=1, open=False)
    await pool.open(wait=True)
    yield pool
    await pool.close()


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
    app = create_app(settings or load(MINIMAL_ENV), keys=keys)
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


# Tokens for any user, for tests that need more than the contract token's
# one account: signed with a key made for this test session, and verified
# against its public half, served as the core would serve its key set. What
# the brain checks is the same as for the contract token; only the key is
# the tests' own.
SIGNING_KEY = Ed25519PrivateKey.generate()
SIGNING_KID = "brain-tests"


def signing_jwks() -> dict[str, Any]:
    raw = SIGNING_KEY.public_key().public_bytes(
        serialization.Encoding.Raw, serialization.PublicFormat.Raw
    )
    x = base64.urlsafe_b64encode(raw).rstrip(b"=").decode()
    return {
        "keys": [
            {"kty": "OKP", "crv": "Ed25519", "alg": "EdDSA", "kid": SIGNING_KID, "x": x}
        ]
    }


def mint(account_id: UUID) -> str:
    """A token for `account_id`, valid for the next half hour by the real
    clock, which these tests leave alone."""
    now = int(time.time())
    claims = {
        "sub": str(account_id),
        "sid": str(uuid.uuid7()),
        "jti": str(uuid.uuid7()),
        "iat": now,
        "exp": now + 1800,
    }
    return jwt.encode(
        claims, SIGNING_KEY, algorithm="EdDSA", headers={"kid": SIGNING_KID}
    )


class Caller:
    """One user calling the running brain: their token on every request, and
    the platform's origin on every one that changes state."""

    def __init__(self, client: TestClient, user: UUID) -> None:
        self.client = client
        self.user = user
        self.headers = bearer(mint(user))

    def get(self, path: str) -> httpx2.Response:
        return self.client.get(path, headers=self.headers)

    def post(self, path: str, body: object = None) -> httpx2.Response:
        headers = {**self.headers, "origin": PLATFORM}
        if body is None:
            return self.client.post(path, headers=headers)
        return self.client.post(path, headers=headers, json=body)


@pytest.fixture
def serving(database: str, web_dir: Path) -> Iterator[TestClient]:
    """The brain started on this test's database, as brain_app, over the
    tests' own key set, with its lifespan entered so its pool is open."""

    def serves_the_signing_key(request: httpx2.Request) -> httpx2.Response:
        return httpx2.Response(200, json=signing_jwks())

    keys = KeyCache(
        httpx2.AsyncClient(transport=httpx2.MockTransport(serves_the_signing_key)),
        CORE_URL,
    )
    settings = load(
        {"BRAIN_WEB_DIR": str(web_dir), "BRAIN_APP_DATABASE_URL": app_url(database)}
    )
    with TestClient(create_app(settings, keys=keys), base_url=PLATFORM) as client:
        yield client


@pytest.fixture
def calling(serving: TestClient) -> Callable[[UUID], Caller]:
    """`calling(user)`, the running brain as that user."""
    return lambda user: Caller(serving, user)
