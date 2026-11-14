# syntax=docker/dockerfile:1
# The brain's image, built from the repository root, which is the context every image shares:
#   docker build -f Brain/Dockerfile .
# infra/hosted/compose.yaml builds it, starts it, runs its migrations and sets everything it reads,
# see infra/hosted/README.md.

# --- Build: the memory centre, as .github/workflows/web.yml builds it. ---
# design/ui is installed first, since the page installs @platform/ui from it by path.
FROM node:26-trixie-slim AS web
WORKDIR /src
COPY design/ui/package.json design/ui/package-lock.json design/ui/
RUN --mount=type=cache,target=/root/.npm cd design/ui && npm ci
COPY design/ui design/ui
COPY Brain/web/package.json Brain/web/package-lock.json Brain/web/
RUN --mount=type=cache,target=/root/.npm cd Brain/web && npm ci
COPY Brain/web Brain/web
RUN cd Brain/web && npm run build

# --- Build: the virtual environment, from uv.lock. ---
# The Python in .python-version, and the uv .github/workflows/brain.yml pins.
FROM python:3.14-slim-trixie AS build
COPY --from=ghcr.io/astral-sh/uv:0.12.19 /uv /usr/local/bin/uv
# No dev dependencies, bytecode compiled once here rather than on every start, files copied out of
# uv's cache rather than linked to it, and the image's own Python, never a downloaded one.
ENV UV_NO_DEV=1 UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_PYTHON_DOWNLOADS=never
# platform-auth is an editable path dependency, ../libs/python/platform-auth, and the brain is
# installed editable too: `brain migrate` reads Brain/migrations/ beside its src/. So the layout
# under /app is the repository's, and stays the same in the final stage.
COPY libs/python/platform-auth /app/libs/python/platform-auth
WORKDIR /app/Brain
COPY Brain/pyproject.toml Brain/uv.lock Brain/README.md ./
COPY Brain/src src
RUN --mount=type=cache,target=/root/.cache/uv uv sync --locked

# --- Run: the environment, the code, the migrations and the page, as a user that is not root. ---
FROM python:3.14-slim-trixie
RUN useradd --system --no-create-home --shell /usr/sbin/nologin brain
COPY --chmod=a+rX --from=build /app/libs/python/platform-auth/src /app/libs/python/platform-auth/src
COPY --chmod=a+rX --from=build /app/Brain/.venv /app/Brain/.venv
COPY --chmod=a+rX --from=build /app/Brain/src /app/Brain/src
COPY --chmod=a+rX Brain/migrations /app/Brain/migrations
# BRAIN_WEB_DIR's default, web/dist, is relative to the working directory below.
COPY --chmod=a+rX --from=web /src/Brain/web/dist /app/Brain/web/dist
ENV PATH=/app/Brain/.venv/bin:$PATH PYTHONUNBUFFERED=1
WORKDIR /app/Brain
USER brain
# The commands, uvicorn on the manifest's port and `brain migrate`, are in
# infra/hosted/compose.yaml.
