# syntax=docker/dockerfile:1
# The proxy's image, built from the repository root, which is the context every image shares:
#   docker build -f infra/hosted/proxy/Dockerfile .
# Caddy with infra/hosted/Caddyfile, the design files it serves and the built component gallery,
# so the look is part of what is released. See infra/hosted/README.md.

# --- Build: the component gallery, as .github/workflows/web.yml builds design/ui. ---
FROM node:26-trixie-slim AS gallery
WORKDIR /src/design/ui
COPY design/ui/package.json design/ui/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm npm ci
COPY design/ui ./
RUN npm run build

# --- Run: Caddy, as a user that is not root. ---
# The image's caddy binary carries cap_net_bind_service, so port 80 needs no root.
FROM caddy:2
RUN addgroup -S proxy && adduser -S -G proxy -H proxy && \
    mkdir -p /config /data && chown proxy:proxy /config /data
COPY --chmod=a+rX infra/hosted/Caddyfile /etc/caddy/Caddyfile
# Exactly the paths the Caddyfile's @design matcher serves, at the paths the dev stack mounts them,
# /srv/design; infra/hosted/tests/agree.test.sh holds the two lists together.
COPY --chmod=a+rX design/tokens.css design/fonts.css design/page.css design/theme.js design/nav.js /srv/design/
COPY --chmod=a+rX design/fonts /srv/design/fonts
COPY --chmod=a+rX design/sprites /srv/design/sprites
COPY --chmod=a+rX --from=gallery /src/design/ui/gallery/dist /srv/design/ui/gallery/dist
USER proxy
