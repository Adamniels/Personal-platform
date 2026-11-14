# syntax=docker/dockerfile:1
# The core's image, built from the repository root, which is the context every image shares:
#   docker build -f Core/Dockerfile .
# infra/hosted/compose.yaml builds it and sets everything it reads, see infra/hosted/README.md.

# --- Build: the release binary. ---
# The version in Core/rust-toolchain.toml. That file is not copied, since rustup would then fetch
# the rustfmt and clippy components it names, which building never uses.
FROM rust:1.98.1-slim-trixie AS build
WORKDIR /src/Core
# The query macros check against the offline cache in .sqlx, since no database exists here.
ENV SQLX_OFFLINE=true
COPY Core/Cargo.toml Core/Cargo.lock Core/build.rs Core/sqlx.toml ./
COPY Core/.sqlx .sqlx
COPY Core/migrations migrations
COPY Core/templates templates
COPY Core/assets assets
COPY Core/src src
# The registry and the target directory are cache mounts, so a rebuild compiles only what
# changed; the binary is copied out of the mount, which is not part of the image.
RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/src/Core/target \
    cargo build --release --locked && cp target/release/platform_core /platform_core

# --- Run: the binary, the hosted registry, and a user that is not root. ---
# The templates, the stylesheet and the migrations are compiled into the binary.
FROM debian:trixie-slim
RUN useradd --system --no-create-home --shell /usr/sbin/nologin core
COPY --chmod=a+rX --from=build /platform_core /usr/local/bin/platform_core
# Read through CORE_FEATURE_REGISTRY, which the compose file points here.
COPY --chmod=a+rX infra/hosted/features.toml /app/features.toml
WORKDIR /app
USER core
# `serve` with no arguments; `migrate`, `invite`, `reset <email>` and `keygen` as the command.
ENTRYPOINT ["platform_core"]
