"""The brain as an ASGI app. Every route sits under `/brain/api/`, the prefix
the Caddyfile sends here, kept. See Brain/README.md."""

from collections.abc import AsyncIterator, Awaitable, Callable
from contextlib import asynccontextmanager
from typing import Any, Literal
from uuid import UUID

import httpx2
from fastapi import APIRouter, FastAPI, Request
from fastapi.responses import JSONResponse, Response
from platform_auth import auth
from platform_auth.keys import KeyCache
from platform_auth.origin import OriginCheck
from platform_auth.verify import Claims
from pydantic import BaseModel

from brain.settings import SETTINGS, Settings


class Health(BaseModel):
    status: Literal["ok"]


class WhoAmI(BaseModel):
    # Claims.account_id, the user id everywhere in the brain.
    account_id: UUID


# What every route behind a token may also answer, for the schema.
UNAUTHORIZED: dict[int | str, dict[str, Any]] = {
    401: {
        "description": (
            "No valid token: missing, expired or failing verification, all"
            " alike. The body is empty; the caller refreshes and retries."
        )
    }
}


def authenticated(
    endpoint: auth.Endpoint,
) -> Callable[[Request], Awaitable[Response]]:
    """An endpoint that needs a valid token, through the library's `api`
    wrapper: called with the claims, or answered 401 with an empty body.

    FastAPI decides a handler's parameters from its signature, following
    `__wrapped__`, which `api` points at the endpoint's `(request, claims)`,
    and would read `claims` as a request body. The handler returned here
    takes only the request, which is all FastAPI then passes."""
    guarded = auth.api(endpoint)

    async def handler(request: Request) -> Response:
        return await guarded(request)

    # The route's name and description in the schema are the endpoint's.
    handler.__name__ = endpoint.__name__
    handler.__qualname__ = endpoint.__qualname__
    handler.__doc__ = endpoint.__doc__
    return handler


router = APIRouter(prefix="/brain/api")


@router.get("/health")
async def health() -> Health:
    """Public: answers whenever the process serves, and needs no token."""
    return Health(status="ok")


@router.get("/whoami", response_model=WhoAmI, responses=UNAUTHORIZED)
@authenticated
async def whoami(request: Request, claims: Claims) -> Response:
    """The account the request's token names, from the token alone."""
    body = WhoAmI(account_id=claims.account_id)
    return JSONResponse(body.model_dump(mode="json"))


def create_app(settings: Settings, keys: KeyCache | None = None) -> FastAPI:
    """The brain as an ASGI app. Without `keys`, it creates one HTTP client
    and one key cache over it when it starts and closes the client when it
    stops; tests pass a cache of their own."""

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        if keys is not None:
            yield
            return
        async with httpx2.AsyncClient() as http:
            app.state.http = http
            app.state.keys = KeyCache(http, settings.core_url)
            yield

    # FastAPI's documentation pages and its schema route would sit outside
    # /brain/api/, so they are off. The schema is still generated, by
    # `app.openapi()`, and committed as contracts/brain/openapi.json.
    app = FastAPI(
        title="brain",
        version="0.1.0",
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
        lifespan=lifespan,
    )
    app.include_router(router)
    # In front of everything, routing included.
    app.add_middleware(OriginCheck, origin=settings.public_origin)
    app.state.settings = settings
    if keys is not None:
        # Set before startup, so a test may call the app without entering
        # its lifespan.
        app.state.keys = keys
    return app


# Settings are loaded at import, so a malformed one stops uvicorn before it
# serves.
app = create_app(SETTINGS)
