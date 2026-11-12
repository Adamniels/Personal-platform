import asyncio
import platform
from contextlib import asynccontextmanager
from html import escape

import httpx2
from platform_auth import auth
from platform_auth.keys import KeyCache
from platform_auth.origin import OriginCheck
from platform_auth.verify import Claims
from starlette.applications import Starlette
from starlette.middleware import Middleware
from starlette.requests import Request
from starlette.responses import HTMLResponse, JSONResponse, PlainTextResponse, Response
from starlette.routing import Route

from canary.settings import SETTINGS, DisplayMode, Settings


def render_page(account_id: str) -> str:
    # No style of its own: the ground, text, fonts, theme, the page container
    # and its headings all come from the design files the proxy serves, and
    # page.css holds the nav bar's height so the page does not jump when
    # /design/nav.js renders it. It takes nothing from the core but the nav's
    # contents.
    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Canary · Platform</title>
  <link rel="stylesheet" href="/design/tokens.css">
  <link rel="stylesheet" href="/design/fonts.css">
  <link rel="stylesheet" href="/design/page.css">
  <script src="/design/theme.js"></script>
  <script src="/design/nav.js" defer></script>
</head>
<body>
  <platform-nav current="canary"></platform-nav>
  <main class="page">
    <h1>Canary</h1>
    <p>Your token was verified in Python. It says you are account <code>{escape(account_id)}</code>.</p>
  </main>
</body>
</html>
"""


# How long the display endpoint sleeps in hang mode: far past any deadline, the
# core's half a second included, so the client always gives up first.
HANG_SECONDS = 3600


@auth.page
async def canary_page(request: Request, claims: Claims) -> HTMLResponse:
    return HTMLResponse(render_page(str(claims.account_id)))


@auth.api
async def whoami(request: Request, claims: Claims) -> JSONResponse:
    return JSONResponse({"account_id": str(claims.account_id)})


async def misbehave(mode: DisplayMode, hang_seconds: float) -> Response | None:
    """What the display endpoint answers in a mode other than normal, or None
    in normal mode. `hang` sleeps for `hang_seconds`, so a client with a
    deadline gives up first, and answers 500 if one is still waiting."""
    if mode is DisplayMode.HANG:
        await asyncio.sleep(hang_seconds)
    if mode is DisplayMode.NORMAL:
        return None
    return PlainTextResponse(
        f"Canary's display endpoint is switched off: CANARY_DISPLAY_MODE={mode}.\n",
        status_code=500,
    )


async def display(request: Request) -> Response:
    """`GET /canary/api/display`. The mode is checked before authentication,
    the way a broken feature would fail, with a token or without."""
    mode = request.app.state.settings.display_mode
    if (failure := await misbehave(mode, request.app.state.hang_seconds)) is not None:
        return failure
    return await display_data(request)


@auth.api
async def display_data(request: Request, claims: Claims) -> JSONResponse:
    """Canary's data for the front page: what it knows, which is that it
    verified this person's token. Data only, never presentation; the core
    decides how it looks. Its shape is pinned by
    `contracts/display/canary.json`."""
    return JSONResponse(
        {
            "account_id": str(claims.account_id),
            "token_expires_at": claims.expires_at,
            "key_id": claims.key_id,
            "python_version": platform.python_version(),
        }
    )


def create_app(
    settings: Settings,
    keys: KeyCache | None = None,
    hang_seconds: float = HANG_SECONDS,
) -> Starlette:
    """Canary as an ASGI app. Without `keys`, it creates one HTTP client and
    one key cache over it when it starts and closes the client when it stops;
    tests pass a cache of their own."""

    @asynccontextmanager
    async def lifespan(app: Starlette):
        if keys is not None:
            yield
            return
        async with httpx2.AsyncClient() as http:
            app.state.http = http
            app.state.keys = KeyCache(http, settings.core_url)
            yield

    app = Starlette(
        routes=[
            Route("/canary/api/whoami", whoami, methods=["GET"]),
            Route("/canary/api/display", display, methods=["GET"]),
            Route("/canary", canary_page, methods=["GET"]),
        ],
        # In front of everything, routing included.
        middleware=[Middleware(OriginCheck, origin=settings.public_origin)],
        lifespan=lifespan,
    )
    app.state.settings = settings
    app.state.hang_seconds = hang_seconds
    if keys is not None:
        # Set before startup, so a test may call the app without entering
        # its lifespan.
        app.state.keys = keys
    return app


# Settings are loaded at import, so a malformed one stops uvicorn before it
# serves.
app = create_app(SETTINGS)
