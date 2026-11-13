"""The brain as an ASGI app: its API under `/brain/api/`, and the memory
centre's frontend at `/brain` and the addresses of its sections, under the
prefix the Caddyfile sends here, kept. See Brain/README.md."""

from collections.abc import AsyncIterator, Awaitable, Callable
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, Literal
from uuid import UUID

import httpx2
from fastapi import APIRouter, FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse, Response
from fastapi.staticfiles import StaticFiles
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


Handler = Callable[[Request], Awaitable[Response]]


def _guarded(
    guard: Callable[[auth.Endpoint], Handler], endpoint: auth.Endpoint
) -> Handler:
    """An endpoint behind one of the library's wrappers, as a handler FastAPI
    calls with the request alone.

    FastAPI decides a handler's parameters from its signature, following
    `__wrapped__`, which the wrappers point at the endpoint's
    `(request, claims)`, and would read `claims` as a request body. The
    handler returned here takes only the request, which is all FastAPI then
    passes."""
    wrapped = guard(endpoint)

    async def handler(request: Request) -> Response:
        return await wrapped(request)

    # The route's name and description in the schema are the endpoint's.
    handler.__name__ = endpoint.__name__
    handler.__qualname__ = endpoint.__qualname__
    handler.__doc__ = endpoint.__doc__
    return handler


def authenticated(endpoint: auth.Endpoint) -> Handler:
    """An API endpoint that needs a valid token, through the library's `api`
    wrapper: called with the claims, or answered 401 with an empty body."""
    return _guarded(auth.api, endpoint)


def page(endpoint: auth.Endpoint) -> Handler:
    """A page a browser loads, through the library's `page` wrapper: called
    with the claims, or answered with a 303 to the core's refresh endpoint and
    the way back."""
    return _guarded(auth.page, endpoint)


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


# The memory centre: one page, index.html from the build, for /brain and for
# /brain/<section>, which the application reads to decide what to show, an
# unknown section included. The built files are mounted at /brain/assets/.
pages = APIRouter(include_in_schema=False)

# Single segments under /brain/ that are never a section.
NOT_SECTIONS = frozenset({"api", "assets"})


@page
async def memory_centre(request: Request, claims: Claims) -> Response:
    """The application's index.html, revalidated on every load, so a new
    build is seen at once."""
    web_dir: Path = request.app.state.settings.web_dir
    return FileResponse(web_dir / "index.html", headers={"cache-control": "no-cache"})


@pages.get("/brain")
async def application(request: Request) -> Response:
    return await memory_centre(request)


@pages.get("/brain/{section}")
async def section(request: Request) -> Response:
    # Before the session is checked, so these answer the same to everyone.
    if request.path_params["section"] in NOT_SECTIONS:
        raise HTTPException(status_code=404)
    return await memory_centre(request)


def check_build(web_dir: Path) -> None:
    """Stops the brain at start when BRAIN_WEB_DIR holds no build, rather
    than serving a page that is not there."""
    if not (web_dir / "index.html").is_file() or not (web_dir / "assets").is_dir():
        raise RuntimeError(
            f"BRAIN_WEB_DIR: {web_dir} holds no build of the memory centre:"
            " run npm run build in Brain/web, see Brain/README.md"
        )


def create_app(settings: Settings, keys: KeyCache | None = None) -> FastAPI:
    """The brain as an ASGI app. Without `keys`, it creates one HTTP client
    and one key cache over it when it starts and closes the client when it
    stops; tests pass a cache of their own."""

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        check_build(settings.web_dir)
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
    app.include_router(pages)
    # Code, never data, so no session is needed. Checked when the app starts,
    # by check_build, rather than here, so the app can be built without one.
    app.mount(
        "/brain/assets",
        StaticFiles(directory=settings.web_dir / "assets", check_dir=False),
        name="assets",
    )
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
