# canary

The platform's first feature, written in Python on purpose. Why it exists, and why not in Rust, is
in "The first milestone" in `High-level/docs/platform-architecture.md`.

## Running it

From this directory, with uv installed:

```zsh
uv run uvicorn canary.app:app --host 127.0.0.1 --port 8081 --reload
uv run pytest
```

`uv run` builds `.venv` from `uv.lock` on first use. Port 8081 sits beside the core's 8080, and the
Caddyfile's upstream for `/canary` points at it. It is a flag until canary reads its configuration
from the environment.

## Libraries canary is built on

**uv for the toolchain.** Chosen because it keeps a real lockfile, `uv.lock`, with hashes for every
platform, which "One repository" in `High-level/docs/platform-architecture.md` asks of every
service. It also installs the Python version pinned in `.python-version`. pip with a venv was the
alternative, more universal, but a pinned `requirements.txt` is a weaker lock and closing the gap
means adding pip-tools.

**A packaged `src/` layout.** The code lives in `src/canary/` and is installed into `.venv` in
editable mode, so the tests and uvicorn both import `canary` from any directory. A flat layout with
a `main.py` would work only from this directory, which is the kind of assumption that holds locally
and fails in CI.

**Starlette for HTTP.** Chosen for the same reason as axum in the core: routing, requests and
middleware with little machinery hiding the request pipeline, and middleware as plain ASGI apps
wrapping each other, the way tower layers do. FastAPI was the close alternative. It is Starlette
with pydantic validation, dependency injection and OpenAPI added, none of which canary needs, since
it validates no request bodies. A feature that does can choose FastAPI without changing any
middleware written against Starlette.

**uvicorn as the server.** Python separates the server from the application through ASGI, so the
app needs one. Plain `uvicorn` rather than `uvicorn[standard]`, whose faster event loop and file
watching do not matter here.

**pytest for tests**, with `httpx2`, which Starlette's `TestClient` uses to call the app in process.
Plain `httpx` still works but is deprecated for this since Starlette 1.7.
