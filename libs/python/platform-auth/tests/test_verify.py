import base64
import hashlib
import hmac
import json
from pathlib import Path
from typing import Any
from uuid import UUID

import jwt
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import (
    Ed25519PrivateKey,
    Ed25519PublicKey,
)

from platform_auth.verify import LEEWAY_SECONDS, Claims, Reason, Refused, verify

# The contract fixture the core's own test pins, at the repository root.
CONTRACT = Path(__file__).resolve().parents[4] / "contracts" / "token"

# Long past, so anything consulting the system clock rather than the time
# passed in would find every token here expired.
NOW = 1_000_000_000
LIFETIME = 1800
KID = "trusted"


def b64(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def unb64(text: str) -> bytes:
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


Keys = dict[str, Ed25519PublicKey]


def segment(value: dict[str, Any]) -> str:
    return b64(json.dumps(value).encode())


def committed() -> tuple[str, dict[str, Any], Keys]:
    token = (CONTRACT / "token.jwt").read_text()
    claims = json.loads((CONTRACT / "claims.json").read_text())
    (jwk,) = json.loads((CONTRACT / "jwks.json").read_text())["keys"]
    key = Ed25519PublicKey.from_public_bytes(unb64(jwk["x"]))
    return token, claims, {jwk["kid"]: key}


def wire(**changes: object) -> dict[str, object]:
    claims = {
        "sub": "01900000-0000-7000-8000-00000000000a",
        "sid": "01900000-0000-7000-8000-00000000000b",
        "jti": "01900000-0000-7000-8000-00000000000c",
        "iat": NOW,
        "exp": NOW + LIFETIME,
    }
    claims.update(changes)
    return {name: value for name, value in claims.items() if value is not None}


@pytest.fixture
def signing_key() -> Ed25519PrivateKey:
    return Ed25519PrivateKey.generate()


@pytest.fixture
def keys(signing_key: Ed25519PrivateKey) -> Keys:
    return {KID: signing_key.public_key()}


def signed(
    key: Ed25519PrivateKey, header: dict[str, Any], payload: dict[str, Any]
) -> str:
    """A token built by hand, so a test controls every byte of it."""
    signing_input = f"{segment(header)}.{segment(payload)}"
    return f"{signing_input}.{b64(key.sign(signing_input.encode()))}"


def mint(
    key: Ed25519PrivateKey, payload: dict[str, Any] | None = None, kid: str = KID
) -> str:
    return jwt.encode(
        wire() if payload is None else payload,
        key,
        algorithm="EdDSA",
        headers={"kid": kid},
    )


def verify_raises(token: str, keys: Keys, now: int) -> Refused:
    with pytest.raises(Refused) as caught:
        verify(token, keys, now)
    return caught.value


def refused(token: str, keys: Keys, now: int = NOW) -> Reason:
    return verify_raises(token, keys, now).reason


def test_the_committed_token_verifies_with_its_claims() -> None:
    token, expected, keys = committed()

    assert verify(token, keys, expected["iat"]) == Claims(
        account_id=UUID(expected["sub"]),
        family_id=UUID(expected["sid"]),
        token_id=UUID(expected["jti"]),
        issued_at=expected["iat"],
        expires_at=expected["exp"],
        key_id=expected["kid"],
    )


def test_expiry_is_accepted_at_exp_plus_five_and_refused_at_six() -> None:
    token, expected, keys = committed()
    exp = expected["exp"]

    assert LEEWAY_SECONDS == 5
    assert verify(token, keys, exp).expires_at == exp
    assert verify(token, keys, exp + 5).expires_at == exp
    assert refused(token, keys, exp + 6) == Reason.EXPIRED


def test_a_bad_signature_is_refused(signing_key: Ed25519PrivateKey, keys: Keys) -> None:
    header, payload, signature = mint(signing_key).split(".")

    # The control: the token as minted verifies.
    assert verify(f"{header}.{payload}.{signature}", keys, NOW)

    altered_payload = segment(wire(sub="01900000-0000-7000-8000-0000000000ff"))
    assert refused(f"{header}.{altered_payload}.{signature}", keys) == Reason.INVALID

    flipped = bytearray(unb64(signature))
    flipped[0] ^= 1
    assert refused(f"{header}.{payload}.{b64(bytes(flipped))}", keys) == Reason.INVALID

    # Signed by another key, under the trusted key's id.
    other = mint(Ed25519PrivateKey.generate(), kid=KID)
    assert refused(other, keys) == Reason.INVALID


def test_alg_none_is_refused(signing_key: Ed25519PrivateKey, keys: Keys) -> None:
    _, payload, signature = mint(signing_key).split(".")

    for alg in ["none", "None", "NONE"]:
        header = segment({"typ": "JWT", "alg": alg, "kid": KID})
        assert refused(f"{header}.{payload}.{signature}", keys) == Reason.INVALID
        assert refused(f"{header}.{payload}.", keys) == Reason.INVALID


def test_every_algorithm_but_eddsa_is_refused(
    signing_key: Ed25519PrivateKey, keys: Keys
) -> None:
    # Signed with the real key, so the algorithm is the only thing wrong.
    def with_alg(alg: str) -> str:
        return signed(signing_key, {"typ": "JWT", "alg": alg, "kid": KID}, wire())

    assert verify(with_alg("EdDSA"), keys, NOW)

    for alg in [
        "HS256", "HS384", "HS512", "RS256", "PS256", "ES256", "eddsa", "Ed25519", "",
    ]:  # fmt: skip
        assert refused(with_alg(alg), keys) == Reason.INVALID, alg


def test_hs256_keyed_with_the_public_key_is_refused(
    signing_key: Ed25519PrivateKey, keys: Keys
) -> None:
    public = signing_key.public_key()
    raw = public.public_bytes(
        serialization.Encoding.Raw, serialization.PublicFormat.Raw
    )
    pem = public.public_bytes(
        serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo
    )

    # Built by hand rather than with PyJWT, which refuses to sign with a key
    # that looks asymmetric: this tests canary's refusal, not the library's
    # guard on the signing side. Each form a verifier might hold the key in.
    for secret in [raw, b64(raw).encode(), pem]:
        signing_input = (
            f"{segment({'typ': 'JWT', 'alg': 'HS256', 'kid': KID})}.{segment(wire())}"
        )
        mac = hmac.new(secret, signing_input.encode(), hashlib.sha256).digest()
        forged = f"{signing_input}.{b64(mac)}"

        assert refused(forged, keys) == Reason.INVALID


def test_an_unknown_or_missing_key_id_is_refused(
    signing_key: Ed25519PrivateKey, keys: Keys
) -> None:
    payload = wire()

    assert refused(mint(signing_key, kid="unknown"), keys) == Reason.INVALID
    assert (
        refused(signed(signing_key, {"alg": "EdDSA"}, payload), keys) == Reason.INVALID
    )
    for kid in [None, 7, "", ["trusted"]]:
        header = {"alg": "EdDSA", "kid": kid}
        assert refused(signed(signing_key, header, payload), keys) == Reason.INVALID
    assert refused(mint(signing_key), {}) == Reason.INVALID


def test_missing_or_malformed_claims_are_refused(
    signing_key: Ed25519PrivateKey, keys: Keys
) -> None:
    for payload in [
        wire(exp=None),
        wire(sub=None),
        wire(sid=None),
        wire(jti=None),
        wire(iat=None),
        wire(sub="not-a-uuid"),
        wire(sid="not-a-uuid"),
        wire(jti="not-a-uuid"),
        wire(sub=""),
        wire(sub=7),
        wire(sub="{01900000-0000-7000-8000-00000000000a}"),
        wire(sub="019000000000700080000000000000000a"),
        wire(exp=str(NOW + LIFETIME)),
        wire(exp=NOW + LIFETIME + 0.5),
        wire(exp=True),
        wire(iat="now"),
    ]:
        assert refused(mint(signing_key, payload), keys) == Reason.INVALID, payload

    # A payload that is not an object at all.
    for body in [b"[]", b"7", b"not json"]:
        signing_input = f"{segment({'alg': 'EdDSA', 'kid': KID})}.{b64(body)}"
        token = f"{signing_input}.{b64(signing_key.sign(signing_input.encode()))}"
        assert refused(token, keys) == Reason.INVALID, body


def test_what_is_not_a_token_is_refused(keys: Keys) -> None:
    for token in ["", "a", "a.b", "a.b.c", "..", "x.y.z.w", "é.é.é", " "]:
        assert refused(token, keys) == Reason.INVALID, token


def test_every_refusal_looks_the_same_to_a_caller(
    signing_key: Ed25519PrivateKey, keys: Keys
) -> None:
    expired = verify_raises(mint(signing_key), keys, NOW + LIFETIME + 6)
    invalid = verify_raises("a.b.c", keys, NOW)

    assert (expired.reason, invalid.reason) == (Reason.EXPIRED, Reason.INVALID)
    assert str(expired) == str(invalid)
    assert type(expired) is type(invalid) is Refused
