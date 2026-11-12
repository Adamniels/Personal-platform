"""The core's public keys, fetched from its key set endpoint and cached in
memory, by the rule in "How a feature authenticates" in
`High-level/docs/feature-contract.md`: a key id the cache does not know means
fetching the set once more before failing, which is what lets the core rotate
keys, but at most once every thirty seconds, so a stream of made up key ids
cannot turn every request into a call to the core.
"""

import asyncio
import base64
import binascii
import re

import httpx2
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

# Served by the core, public and without a session. See "The set is served at
# GET /.well-known/jwks.json" in Core/docs/core-architecture.md.
JWKS_PATH = "/.well-known/jwks.json"

REFETCH_COOLDOWN_SECONDS = 30
FETCH_TIMEOUT_SECONDS = 2

# base64url without padding, as RFC 7515 writes it.
BASE64URL = re.compile(r"[A-Za-z0-9_-]*")


class KeyCache:
    """The key set, by key id. `client` makes the requests, so tests can
    answer them from memory; `core_url` is the core's own address, never the
    proxy's. Time is passed in, whole seconds since the epoch."""

    def __init__(self, client: httpx2.AsyncClient, core_url: str):
        self._client = client
        self._url = core_url + JWKS_PATH
        self._keys: dict[str, Ed25519PublicKey] = {}
        self._last_fetch: int | None = None
        self._lock = asyncio.Lock()

    async def find(self, key_id: str, now: int) -> Ed25519PublicKey | None:
        """The key with `key_id`, fetching the set first if it is not cached
        and the last fetch, successful or not, was not inside the cooldown.
        None if the set, as last fetched, does not hold it."""
        if (key := self._keys.get(key_id)) is not None:
            return key
        async with self._lock:
            # A fetch that ran while this one waited may have brought it.
            if (key := self._keys.get(key_id)) is not None:
                return key
            if (
                self._last_fetch is not None
                and now - self._last_fetch < REFETCH_COOLDOWN_SECONDS
            ):
                return None
            self._last_fetch = now
            fetched = await self._fetch()
            if fetched is not None:
                self._keys = fetched
            return self._keys.get(key_id)

    async def _fetch(self) -> dict[str, Ed25519PublicKey] | None:
        """The set the core serves now, or None if it could not be had: the
        network, a status other than 200, or a body that is not a key set."""
        try:
            response = await self._client.get(self._url, timeout=FETCH_TIMEOUT_SECONDS)
            if response.status_code != 200:
                return None
            return parse_key_set(response.json())
        except httpx2.HTTPError, ValueError:
            return None


def parse_key_set(body) -> dict[str, Ed25519PublicKey] | None:
    """The Ed25519 keys in a JWK set (RFC 7517 and RFC 8037), by key id, or
    None if `body` is not a set. Entries of any other kind, and entries that
    do not parse, are ignored."""
    if not isinstance(body, dict) or not isinstance(body.get("keys"), list):
        return None
    keys = {}
    for entry in body["keys"]:
        if not isinstance(entry, dict):
            continue
        if entry.get("kty") != "OKP" or entry.get("crv") != "Ed25519":
            continue
        kid, x = entry.get("kid"), entry.get("x")
        if not isinstance(kid, str) or not isinstance(x, str):
            continue
        if (key := _public_key(x)) is not None:
            keys[kid] = key
    return keys


def _public_key(x: str) -> Ed25519PublicKey | None:
    if not BASE64URL.fullmatch(x):
        return None
    try:
        raw = base64.urlsafe_b64decode(x + "=" * (-len(x) % 4))
        return Ed25519PublicKey.from_public_bytes(raw)
    except binascii.Error, ValueError:
        return None
