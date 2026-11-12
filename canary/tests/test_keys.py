import asyncio
import base64
import json
from pathlib import Path

import httpx2
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

from canary.keys import JWKS_PATH, REFETCH_COOLDOWN_SECONDS, KeyCache

# The contract fixture: the key set exactly as the core serves it.
JWKS = json.loads(
    (Path(__file__).resolve().parents[2] / "contracts/token/jwks.json").read_text()
)
KID = JWKS["keys"][0]["kid"]
CORE_URL = "http://127.0.0.1:8080"


def raw(key) -> bytes:
    return key.public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)


def jwk(kid: str, key=None) -> dict:
    key = key or Ed25519PrivateKey.generate().public_key()
    x = base64.urlsafe_b64encode(raw(key)).rstrip(b"=").decode()
    return {"kty": "OKP", "crv": "Ed25519", "alg": "EdDSA", "kid": kid, "x": x}


class Core:
    """The core's key set endpoint, answered from memory, counting requests."""

    def __init__(self, answer=None):
        self.requests: list[httpx2.Request] = []
        self.answer = answer or (lambda request: httpx2.Response(200, json=JWKS))

    def handle(self, request: httpx2.Request):
        self.requests.append(request)
        return self.answer(request)

    def cache(self) -> KeyCache:
        client = httpx2.AsyncClient(transport=httpx2.MockTransport(self.handle))
        return KeyCache(client, CORE_URL)


def unreachable(request):
    raise httpx2.ConnectError("connection refused", request=request)


def timed_out(request):
    raise httpx2.ReadTimeout("timed out", request=request)


def run(scenario):
    asyncio.run(scenario())


def test_the_key_set_is_fetched_once_and_cached():
    core = Core()
    cache = core.cache()

    async def scenario():
        first = await cache.find(KID, 0)
        second = await cache.find(KID, 1)

        assert first is not None and second is first
        x = base64.urlsafe_b64decode(JWKS["keys"][0]["x"] + "=")
        assert raw(first) == x

    run(scenario)
    (request,) = core.requests
    assert request.method == "GET"
    assert str(request.url) == f"{CORE_URL}/.well-known/jwks.json"
    assert JWKS_PATH == "/.well-known/jwks.json"
    assert request.extensions["timeout"]["read"] == 2


def test_an_unknown_key_id_refetches_once_then_fails():
    core = Core()
    cache = core.cache()

    async def scenario():
        assert await cache.find(KID, 0) is not None
        assert len(core.requests) == 1

        assert await cache.find("unknown", 100) is None
        assert len(core.requests) == 2

        # A known key is still found without asking again.
        assert await cache.find(KID, 101) is not None
        assert len(core.requests) == 2

    run(scenario)


def test_a_key_the_core_starts_serving_is_found_by_the_refetch():
    rotated = {"keys": [*JWKS["keys"], jwk("next")]}
    core = Core()
    cache = core.cache()

    async def scenario():
        assert await cache.find(KID, 0) is not None
        core.answer = lambda request: httpx2.Response(200, json=rotated)

        assert await cache.find("next", 100) is not None
        assert await cache.find(KID, 101) is not None
        assert len(core.requests) == 2

    run(scenario)


def test_a_second_unknown_key_id_inside_thirty_seconds_does_not_refetch():
    core = Core()
    cache = core.cache()

    async def scenario():
        assert REFETCH_COOLDOWN_SECONDS == 30
        await cache.find(KID, 0)
        await cache.find("first-unknown", 100)
        assert len(core.requests) == 2

        assert await cache.find("second-unknown", 129) is None
        assert len(core.requests) == 2

        assert await cache.find("second-unknown", 130) is None
        assert len(core.requests) == 3

    run(scenario)


def test_the_first_lookup_counts_for_the_cooldown():
    core = Core()
    cache = core.cache()

    async def scenario():
        await cache.find(KID, 0)
        assert await cache.find("unknown", 29) is None
        assert len(core.requests) == 1

    run(scenario)


def test_cached_keys_keep_verifying_when_the_core_is_unreachable():
    core = Core()
    cache = core.cache()

    async def scenario():
        known = await cache.find(KID, 0)
        core.answer = unreachable

        assert await cache.find(KID, 1000) is known
        # A refetch that fails leaves the set as it was.
        assert await cache.find("unknown", 1000) is None
        assert len(core.requests) == 2
        assert await cache.find(KID, 1001) is known

    run(scenario)


@pytest.mark.parametrize(
    "failure",
    [
        unreachable,
        timed_out,
        lambda request: httpx2.Response(500, json=JWKS),
        lambda request: httpx2.Response(404),
        lambda request: httpx2.Response(200, text="not json"),
        lambda request: httpx2.Response(200, json=["not", "a", "key", "set"]),
        lambda request: httpx2.Response(200, json={"keys": "none"}),
        lambda request: httpx2.Response(200, json={"no keys": []}),
    ],
    ids=[
        "unreachable",
        "timeout",
        "500",
        "404",
        "not json",
        "a list",
        "keys not a list",
        "no keys",
    ],
)
def test_a_failed_fetch_finds_no_key_and_is_retried_after_the_cooldown(failure):
    core = Core(answer=failure)
    cache = core.cache()

    async def scenario():
        assert await cache.find(KID, 0) is None
        assert await cache.find(KID, 29) is None
        assert len(core.requests) == 1

        core.answer = lambda request: httpx2.Response(200, json=JWKS)
        assert await cache.find(KID, 30) is not None
        assert len(core.requests) == 2

    run(scenario)


def test_only_ed25519_keys_are_taken():
    good = jwk("good")
    served = {
        "keys": [
            {"kty": "RSA", "kid": "rsa", "n": "AQAB", "e": "AQAB"},
            {**jwk("x25519"), "crv": "X25519"},
            {**jwk("ec"), "kty": "EC"},
            {**jwk("short"), "x": "AAAA"},
            {**jwk("padded"), "x": jwk("padded")["x"] + "="},
            {**jwk("no-x"), "x": None},
            {**jwk("kid-not-a-string"), "kid": 7},
            "not an object",
            good,
        ]
    }
    core = Core(answer=lambda request: httpx2.Response(200, json=served))
    cache = core.cache()

    async def scenario():
        assert await cache.find("good", 0) is not None
        for kid in ["rsa", "x25519", "ec", "short", "padded", "no-x"]:
            assert await cache.find(kid, 0) is None, kid

    run(scenario)


def test_one_fetch_runs_at_a_time():
    core = Core()
    cache = core.cache()

    async def slow(request):
        # Yield mid fetch, so the other lookups run while this one waits.
        await asyncio.sleep(0.01)
        return httpx2.Response(200, json=JWKS)

    core.answer = slow

    async def scenario():
        found = await asyncio.gather(*(cache.find(KID, 0) for _ in range(5)))
        assert all(key is not None for key in found)

    run(scenario)
    assert len(core.requests) == 1
