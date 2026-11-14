# syntax=docker/dockerfile:1
# Canary's image, built from the repository root, which is the context every image shares:
#   docker build -f canary/Dockerfile .
# infra/hosted/compose.yaml builds it, starts it and sets everything it reads, see
# infra/hosted/README.md.

# --- Build: the virtual environment, from uv.lock. ---
# The Python in .python-version, and the uv .github/workflows/canary.yml pins.
FROM python:3.14-slim-trixie AS build
COPY --from=ghcr.io/astral-sh/uv:0.12.19 /uv /usr/local/bin/uv
# No dev dependencies, bytecode compiled once here rather than on every start, files copied out of
# uv's cache rather than linked to it, and the image's own Python, never a downloaded one.
ENV UV_NO_DEV=1 UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_PYTHON_DOWNLOADS=never
# platform-auth is an editable path dependency, ../libs/python/platform-auth, so the layout under
# /app is the repository's, and stays the same in the final stage, where the installs point.
COPY libs/python/platform-auth /app/libs/python/platform-auth
WORKDIR /app/canary
COPY canary/pyproject.toml canary/uv.lock canary/README.md ./
COPY canary/src src
RUN --mount=type=cache,target=/root/.cache/uv uv sync --locked

# --- Run: the environment and the code it imports, as a user that is not root. ---
FROM python:3.14-slim-trixie
RUN useradd --system --no-create-home --shell /usr/sbin/nologin canary
COPY --chmod=a+rX --from=build /app/libs/python/platform-auth/src /app/libs/python/platform-auth/src
COPY --chmod=a+rX --from=build /app/canary/.venv /app/canary/.venv
COPY --chmod=a+rX --from=build /app/canary/src /app/canary/src
ENV PATH=/app/canary/.venv/bin:$PATH PYTHONUNBUFFERED=1
WORKDIR /app/canary
USER canary
# The command, uvicorn on the manifest's port, is in infra/hosted/compose.yaml.
