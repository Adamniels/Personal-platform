"""The brain as an ASGI app. Every route sits under `/brain/api/`, the prefix
the Caddyfile sends here, kept. See Brain/README.md."""

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Literal

from fastapi import APIRouter, FastAPI
from pydantic import BaseModel

from brain import db
from brain.settings import SETTINGS, Settings


class Health(BaseModel):
    status: Literal["ok"]


router = APIRouter(prefix="/brain/api")


@router.get("/health")
async def health() -> Health:
    """Public: answers whenever the process serves, and needs no token."""
    return Health(status="ok")


def create_app(settings: Settings) -> FastAPI:
    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        # The pool lives as long as the app, and is reached only through
        # brain.db.transaction. Migrations never run here: the app role does
        # not own the tables, see "Migrating" in Brain/README.md.
        pool = await db.open_pool(settings.app_database_url)
        app.state.pool = pool
        try:
            yield
        finally:
            await pool.close()

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
    app.state.settings = settings
    return app


# Settings are loaded at import, so a malformed one stops uvicorn before it
# serves.
app = create_app(SETTINGS)
