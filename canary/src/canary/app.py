from contextlib import asynccontextmanager
from html import escape

import httpx2
from starlette.applications import Starlette
from starlette.middleware import Middleware
from starlette.requests import Request
from starlette.responses import HTMLResponse, JSONResponse
from starlette.routing import Route

from canary import auth
from canary.keys import KeyCache
from canary.origin import OriginCheck
from canary.settings import SETTINGS, Settings
from canary.verify import Claims

# Canary's own few lines of style. It takes nothing from the core but the nav,
# so it reserves the bar's height itself, as the core's stylesheet does, and
# the page does not jump when /nav.js renders it.
STYLE = """
:root { color-scheme: light dark; font-family: system-ui, sans-serif; line-height: 1.5; }
body { margin: 0; }
platform-nav { display: block; min-height: 3rem; }
main { max-width: 40rem; margin: 0 auto; padding: 3rem 1rem; }
h1 { font-size: 1.5rem; margin: 0 0 1.5rem; }
code { overflow-wrap: anywhere; }
"""


def render_page(account_id: str) -> str:
    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Canary · Platform</title>
  <style>{STYLE}</style>
  <script src="/nav.js" defer></script>
</head>
<body>
  <platform-nav current="canary"></platform-nav>
  <main>
    <h1>Canary</h1>
    <p>Your token was verified in Python. It says you are account <code>{escape(account_id)}</code>.</p>
  </main>
</body>
</html>
"""


@auth.page
async def canary_page(request: Request, claims: Claims) -> HTMLResponse:
    return HTMLResponse(render_page(str(claims.account_id)))


@auth.api
async def whoami(request: Request, claims: Claims) -> JSONResponse:
    return JSONResponse({"account_id": str(claims.account_id)})


def create_app(settings: Settings, keys: KeyCache | None = None) -> Starlette:
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
            Route("/canary", canary_page, methods=["GET"]),
        ],
        # In front of everything, routing included.
        middleware=[Middleware(OriginCheck, origin=settings.public_origin)],
        lifespan=lifespan,
    )
    app.state.settings = settings
    if keys is not None:
        # Set before startup, so a test may call the app without entering
        # its lifespan.
        app.state.keys = keys
    return app


# Settings are loaded at import, so a malformed one stops uvicorn before it
# serves.
app = create_app(SETTINGS)
