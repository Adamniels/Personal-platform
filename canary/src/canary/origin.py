"""The `Origin` check: every request that can change state must come from the
platform's own origin. With `SameSite=Lax` and never changing state on a GET,
it is the whole CSRF defence, see "How a feature authenticates" in
`High-level/docs/feature-contract.md`. The Python twin of `Core/src/origin.rs`.
"""

from starlette.responses import PlainTextResponse
from starlette.types import ASGIApp, Receive, Scope, Send

SAFE_METHODS = frozenset({"GET", "HEAD", "OPTIONS"})

REFUSAL = "Forbidden: this request did not come from the platform's own origin.\n"


class OriginCheck:
    """ASGI middleware over the whole app, so a route added later is covered
    without anyone remembering to. It runs before routing, and so before a
    body is read. `origin` is the platform's, already parsed by
    `settings.parse_origin` into the form a browser sends."""

    def __init__(self, app: ASGIApp, origin: str):
        self.app = app
        self.origin = origin.encode()

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        # Lifespan passes, and so would a websocket: canary serves none, and
        # one added later needs its own check on the handshake's Origin.
        if scope["type"] != "http" or scope["method"] in SAFE_METHODS:
            await self.app(scope, receive, send)
            return

        # Exactly one Origin, equal byte for byte. No normalising and no
        # prefix match, which is how https://platform.test.evil.test would
        # get through.
        sent = [value for name, value in scope["headers"] if name == b"origin"]
        if sent == [self.origin]:
            await self.app(scope, receive, send)
            return
        await PlainTextResponse(REFUSAL, status_code=403)(scope, receive, send)
