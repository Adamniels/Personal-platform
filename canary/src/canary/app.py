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


async def echo_path(request: Request) -> HTMLResponse:
    path = escape(request.url.path)
    return HTMLResponse(
        f"<!doctype html><title>canary</title><p>canary received <code>{path}</code></p>"
    )


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
            Route("/canary", echo_path),
            Route("/canary/{rest:path}", echo_path),
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
