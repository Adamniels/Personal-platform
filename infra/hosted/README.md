# infra/hosted

The hosted copy of the platform: every service built into an image and started as one compose
project of its own, apart from the dev stack in `infra/compose.yaml`. It is the copy that stays up
on the Mac mini, reached over Tailscale. Development still runs natively on a laptop, see
"Databases, migrations, and running it" in `High-level/docs/platform-architecture.md`.

This folder makes the containers, proves they start, and runs the acceptance suite against them.
Releasing to the real copy and backups arrive in later slices, as does running `check.sh` in CI.

## What runs

| Service    | Image                       | Listens on | Reached                                  |
| ---------- | --------------------------- | ---------- | ---------------------------------------- |
| `postgres` | `postgres:18`               | 5432       | by the services, on the project network  |
| `core`     | `platform-hosted/core`      | 8080       | through the proxy                        |
| `canary`   | `platform-hosted/canary`    | 8081       | through the proxy                        |
| `brain`    | `platform-hosted/brain`     | 8082       | through the proxy                        |
| `review`   | `platform-hosted/review`    | 8083       | through the proxy                        |
| `proxy`    | `platform-hosted/proxy`     | 80         | published, `127.0.0.1:9080` by default   |

The proxy is the only container published, plain HTTP on the loopback. HTTPS is terminated in
front of it, by `tailscale serve` on the real copy and by a test front in `check.sh`, so the proxy
never holds a certificate. Postgres is published nowhere.

Each image is `platform-hosted/<service>:${PLATFORM_RELEASE:-dev}`, so a release can tag by
commit. Each `migrate` in `infra/services.json` has a `<name>-migrate` service in the `migrate`
profile: the service's own image, its migrate command, and the database owner's URL. Migrations
stay a command, never something that happens at startup, and no serving container ever has the
owner's URL.

## The files

- `compose.yaml`, the services above. It has no top level `name`: the project is always given
  with `-p`. The real copy's is passed by the release script of a later slice, and `check.sh`
  passes `hostedcheck`. A compose command typed here without `-p` gets Compose's default, the
  folder's name, `hosted`, which is neither, so a command typed in another clone never reaches
  the real copy by accident.
- `Caddyfile`, the proxy's: the dev Caddyfile's routes on one plain HTTP site, `:80`, with each
  upstream the service's compose name, `canary:8081`, `review:8083`, `brain:8082` and the rest
  `core:8080`, and the same design paths, served from files copied into the proxy's image.
- `features.toml`, the core's registry in the hosted copy: `infra/features.toml` feature for
  feature, with each upstream `http://<service>:<port>`. Two files rather than one with
  placeholders, since the core reads a plain file; `tests/agree.test.sh` holds them together.
- `.env.example`, every variable `compose.yaml` reads, each with what it is. The real copy's
  values go in `.env` beside it, which git ignores.
- `proxy/Dockerfile`, the proxy's image. Each service's Dockerfile is in its own folder:
  `Core/Dockerfile`, `canary/Dockerfile`, `Brain/Dockerfile` and `review/Dockerfile`.
- `check.sh`, with `front/`, its test HTTPS front, and `tests/agree.test.sh`, see below.

A route, a feature or a service added to the dev stack is added here in the same commit:
`agree.test.sh` fails until it is.

## The images

Every image builds from the repository root, since services install `libs/` and `design/ui` by
path; the root `.dockerignore` keeps build output, installed dependencies, `.env` files and `.git`
out of the context. Each Dockerfile is multi stage. The build stages hold the toolchain, at the
version the repository already pins for that language, and install only from the committed
lockfiles: `cargo build --locked`, `uv sync --locked`, `npm ci` and `dotnet restore --locked-mode`,
which `agree.test.sh` checks. The final stage holds only what runs, as a user that is not root,
with every file copied in readable by that user and owned by root, so a service cannot change its
own code. No image holds a `.env` file or a secret: everything a service reads comes from its
environment, by the names it reads natively, see each service's "Settings".

The proxy's image holds the served files of `design/` and the built component gallery, so the
look is part of what is released rather than mounted from a checkout.

## Running it by hand

Until the release script exists, from this folder, with `.env` filled in from `.env.example` and
the project name given with `-p` on every command:

```zsh
docker compose -p <project> build
docker compose -p <project> run --rm --no-deps -T core keygen >> .env
docker compose -p <project> up -d --wait postgres
docker compose -p <project> run --rm core-migrate
docker compose -p <project> run --rm brain-migrate
docker compose -p <project> run --rm review-migrate
docker compose -p <project> up -d
```

The signing key is made once per copy, never copied from another environment, see "The signing
key" in `Core/docs/core-architecture.md`. The databases are created by `infra/postgres/initdb/`
when the volume is first created, the same scripts as the dev stack; each is safe to run again,
see "The databases" in `infra/README.md`.

## check.sh

`check.sh` proves the stack works, on a throwaway copy. From the repository root, with Docker
running, and `uv` and Playwright's Chromium installed for the acceptance suite, which it never
installs (from `e2e/`, `uv run playwright install chromium`), and `platform.test` resolving to
`127.0.0.1`, as for the dev suite:

```zsh
bash infra/hosted/check.sh
```

It runs `tests/agree.test.sh` first, then builds every image, starts the project `hostedcheck`
with passwords and a signing key made for this run in a temporary env file, waits for Postgres,
runs every `*-migrate` service, starts the rest and the test front, and waits up to 180 seconds
for each health path `scripts/agent/services.sh health` lists to answer `200`. The test front,
`front/`, is `caddy:2` with `tls internal`, serving `https://platform.test:9443` on
`127.0.0.1:9443` and proxying to `proxy:80`, standing in for `tailscale serve`; its file is added
with a second `-f` and is no part of the hosted copy. Its images are tagged `check`, so it never
retags one the real copy runs.

It prints `ok <name>` or `FAIL <name>` for each case and exits non zero if any failed:

- `agree_test`, `tests/agree.test.sh` passes;
- `it_refuses_the_real_project`: with `HOSTED_PROJECT=platform-hosted` it exits non zero before
  running any docker command;
- `the_stack_builds_and_every_service_answers`: the build, every migration, every health path
  answering `200` through the front, and `/canary/api/whoami` answering `401`;
- `the_design_files_and_the_gallery_are_served`: `/design/tokens.css` and `/design/gallery/`
  answer `200`, and `/design/DESIGN.md` answers `404`;
- `nothing_but_the_proxy_is_published`: of the project's containers only the proxy and the front
  publish a port, both on `127.0.0.1`;
- `no_container_runs_as_root`: every process of each service in `compose.yaml` runs as a user id
  other than 0. The front, which is not one of them, runs as Caddy's image does;
- `the_acceptance_suite_passes_against_the_containers`: the whole suite in `e2e/`, every test but
  those marked `dev_stack_only`, passes against `https://platform.test:9443`, with invite codes and
  reset tokens made in the core's container through `E2E_CORE_COMMAND`. Its output is shown as it
  runs, and it says so when `uv` or Chromium is missing. A test that fails here because of the
  hosted shape is fixed in the image or the compose file, never marked; see "Markers" in
  `e2e/README.md`. Its screenshots land in `e2e/screenshots/` and are not published;
- `it_leaves_nothing_behind`: after the run, and after a run made to fail once Postgres is up, no
  container, network or volume of the project exists.

It always ends with `docker compose down -v` for its project, by a trap, and removes nothing else:
images and Docker's build cache stay, so a second run builds only what changed. `HOSTED_PROJECT`
names another project, for two runs side by side. It works while the dev stack is up, since it
uses neither its ports, 80, 443 and 5432, nor its project. Later slices add their cases to it.

`tests/agree.test.sh` reads files only and needs no Docker, in the style of
`scripts/ci/services.test.sh`: each manifest entry is a compose service on its port with its
`-migrate` service, no serving service has an owner's URL, the hosted registry and Caddyfile match
the dev ones, the proxy's image holds every design path it serves, every variable the compose file
reads is in `.env.example`, every install in a Dockerfile is locked, and the compose file names no
project. Each check also runs on a broken copy, so it cannot pass by matching nothing.

Neither runs in CI yet; a later slice adds that.

The suite runs last, after the cases above, and takes as long as a dev run of it, several minutes,
on top of the build.

## Resources

Building every image at once needs more memory than Docker Desktop's default; the Mac mini's is
raised to 8 GB. The first build fetches every crate and package, and takes several minutes; later
builds reuse Docker's cache.
