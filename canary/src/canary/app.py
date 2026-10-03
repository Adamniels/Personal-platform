from html import escape

from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import HTMLResponse
from starlette.routing import Route


async def echo_path(request: Request) -> HTMLResponse:
    path = escape(request.url.path)
    return HTMLResponse(
        f"<!doctype html><title>canary</title><p>canary received <code>{path}</code></p>"
    )


app = Starlette(
    routes=[
        Route("/canary", echo_path),
        Route("/canary/{rest:path}", echo_path),
    ]
)
