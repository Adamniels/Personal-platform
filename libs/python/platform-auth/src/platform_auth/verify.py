"""Verifying the core's access tokens: EdDSA only, the key chosen by the
token's key id, and expiry checked here against a time passed in, with the
leeway chosen rather than inherited. The Python twin of `Core/src/verifier.rs`;
the rules are in "How a feature authenticates" in
`High-level/docs/feature-contract.md` and "The clock leeway on verification is
chosen, not inherited" in `Core/docs/core-architecture.md`.
"""

import re
from collections.abc import Mapping
from dataclasses import dataclass
from enum import Enum
from uuid import UUID

import jwt
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
from jwt.types import Options

# How long past its `exp` a token is still accepted, to absorb clock drift
# between machines. The same five seconds in every verifier.
LEEWAY_SECONDS = 5

ALGORITHM = "EdDSA"

# The library checks the algorithm, the signature and that `exp` and `sub` are
# there. Every check of its own that reads the system clock is off, and so is
# its leeway: PyJWT refuses at `exp + 5`, where the contract still accepts, and
# would refuse an `iat` it thinks is in the future. `verify` checks `exp`
# below, against the time it is given. `aud` is not checked, as in the core.
OPTIONS: Options = {
    "verify_signature": True,
    "verify_exp": False,
    "verify_nbf": False,
    "verify_iat": False,
    "verify_aud": False,
    "require": ["exp", "sub"],
}

# The form the core writes every id in. Python's UUID() also takes braces,
# a urn prefix and hyphens anywhere, which the core never sends.
UUID_FORM = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")


class Reason(Enum):
    # Not a token, the wrong algorithm, a missing or unknown key id, a bad
    # signature, or claims missing or malformed.
    INVALID = "invalid"
    EXPIRED = "expired"


class Refused(Exception):
    """A token that is not accepted. Every refusal is handled the same way,
    so `reason` is for logs and tests, never for the response."""

    def __init__(self, reason: Reason):
        super().__init__("token refused")
        self.reason = reason


@dataclass(frozen=True)
class Claims:
    account_id: UUID
    family_id: UUID
    token_id: UUID
    issued_at: int
    expires_at: int
    key_id: str


def verify(token: str, keys: Mapping[str, Ed25519PublicKey], now: int) -> Claims:
    """The claims of `token` if it is valid at `now`, whole seconds since the
    epoch: signed with EdDSA by the key its header names in `keys`, and `now`
    no later than its `exp` plus the leeway. Raises `Refused` otherwise."""
    try:
        key_id = jwt.get_unverified_header(token).get("kid")
        if not isinstance(key_id, str) or (key := keys.get(key_id)) is None:
            raise Refused(Reason.INVALID)
        payload = jwt.decode(token, key, algorithms=[ALGORITHM], options=OPTIONS)
    except jwt.PyJWTError:
        raise Refused(Reason.INVALID) from None

    claims = Claims(
        account_id=_uuid(payload.get("sub")),
        family_id=_uuid(payload.get("sid")),
        token_id=_uuid(payload.get("jti")),
        issued_at=_seconds(payload.get("iat")),
        expires_at=_seconds(payload.get("exp")),
        key_id=key_id,
    )
    if claims.expires_at + LEEWAY_SECONDS < now:
        raise Refused(Reason.EXPIRED)
    return claims


def _uuid(value: object) -> UUID:
    if not isinstance(value, str) or not UUID_FORM.fullmatch(value):
        raise Refused(Reason.INVALID)
    return UUID(value)


def _seconds(value: object) -> int:
    # An integer, as the core writes it: not a float, a string, or a bool,
    # which Python counts as an int.
    if type(value) is not int:
        raise Refused(Reason.INVALID)
    return value
