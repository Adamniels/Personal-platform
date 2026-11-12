"""The brain as an ASGI app. Every route sits under `/brain/api/`, the prefix
the Caddyfile sends here, kept. See Brain/README.md."""

from typing import Literal

from fastapi import APIRouter, FastAPI
from pydantic import BaseModel

from brain.settings import SETTINGS, Settings


class Health(BaseModel):
    status: Literal["ok"]


router = APIRouter(prefix="/brain/api")


@router.get("/health")
async def health() -> Health:
    """Public: answers whenever the process serves, and needs no token."""
    return Health(status="ok")


def create_app(settings: Settings) -> FastAPI:
    # FastAPI's documentation pages and its schema route would sit outside
    # /brain/api/, so they are off. The schema is still generated, by
    # `app.openapi()`, and committed as contracts/brain/openapi.json.
    app = FastAPI(
        title="brain",
        version="0.1.0",
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )
    app.include_router(router)
    app.state.settings = settings
    return app


# Settings are loaded at import, so a malformed one stops uvicorn before it
# serves.
app = create_app(SETTINGS)
