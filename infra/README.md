# infra

What the platform runs on locally: Postgres and Caddy in `compose.yaml`, and the Caddyfile that
routes the hostname to the core, the brain and the features. Why it runs under a real hostname with real
HTTPS from the first day is in "Databases, migrations, and running it" in
`High-level/docs/platform-architecture.md`.

## The hostname

The platform answers on `platform.test`, which resolves to the laptop through a line in
`/etc/hosts`. It is done once per machine, by hand, since it needs sudo:

```zsh
sudo cp /etc/hosts /etc/hosts.backup
sudo vim /etc/hosts
```

and add at the end:

```
# personal-platform local dev, see infra/README.md in the repo
127.0.0.1	platform.test
```

`.test` is reserved and never resolves publicly. `.localhost` would be simpler, but browsers relax
`Secure` cookies there, which would hide the behaviour the hostname exists to prove. There is no
`::1` line, since Caddy is published on `127.0.0.1` only.

To check it, `dscacheutil -q host -a name platform.test` should print `127.0.0.1`. `dig` asks DNS
and never reads the hosts file, so it reports `NXDOMAIN`, which is expected.

## Caddy

Caddy serves `https://platform.test`, routing `/canary` and everything under it to canary,
`/review` and everything under it to review, `/devflow` and everything under it to Dev flow, and
`/brain` and everything under it to the brain, prefix kept in each, serving a few files of the
design folder itself under `/design/` (see "The design folder" below), and routing the rest to the
core. Why Caddy, rather than Traefik or nginx, is in "Routing" in
`Core/docs/core-architecture.md`. The core, canary, the brain, review and Dev flow run natively, on
8080, 8081, 8082, 8083 and 8084, and Caddy runs in compose. From this directory:

```zsh
docker compose up -d caddy
```

The upstreams are `host.docker.internal`, which only Docker Desktop provides. It reaches services
bound to `127.0.0.1` on the Mac, tested, but on Linux neither holds, so this setup is Mac only.
Ports 80 and 443 are published on `127.0.0.1` only, as Postgres is, so the platform is not
reachable from the network. Port 80 only redirects to HTTPS.

Caddy's local certificate authority signs the certificate, through `tls internal`, since `.test`
is not a name Caddy treats as local. Its root, intermediate and private keys live in the
`caddy_data` volume. `docker compose down -v` deletes them, and a new root is generated on the next
start, so the root trusted on the laptop stops working until it is trusted again.

The Caddyfile is mounted as a single file, and editors that save by replacing the file leave the
container reading the old one. After editing it, recreate the container rather than reloading:

```zsh
docker compose run --rm caddy caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
docker compose up -d --force-recreate caddy
```

## The databases

One Postgres, a database per service, each with two roles: an owner that owns the tables and runs
the service's migrations, and an app role the running service connects as. Why, and why the app
role must not own its tables, is in "Databases, migrations, and running it" and "Multi user, and
how isolation is enforced" in `High-level/docs/platform-architecture.md`.

The scripts in `postgres/initdb/` create them, one per service, `core.sh` for `core` with
`core_owner` and `core_app`, `brain.sh` for `brain` with `brain_owner` and `brain_app`,
`review.sh` for `review` with `review_owner` and `review_app`, and `devflow.sh` for `devflow` with
`devflow_owner` and `devflow_app`. Their
passwords come from `.env`, see `.env.example`, through `compose.yaml`, which refuses to start
without them. Postgres runs every script there once, when the volume is first created, and never
again. None creates a table or grants on one: that is each service's migrations.

Every script is safe to run again: each creates its roles and its database only when missing, so
it never changes a password, and applies the database's privileges every time. A volume created
before a service had a database, or one whose database was dropped, gets it by running that
service's script by hand, from this directory, after adding its two passwords to `.env` and
recreating the container so it has them:

```zsh
docker compose up -d --wait
docker compose exec -T postgres bash /docker-entrypoint-initdb.d/brain.sh
docker compose exec -T postgres bash /docker-entrypoint-initdb.d/review.sh
docker compose exec -T postgres bash /docker-entrypoint-initdb.d/devflow.sh
```

It prints `CREATE ROLE`, `CREATE DATABASE` and the rest for what it created, and only `REVOKE` and
`GRANT` when everything was there. The hosted copy mounts the same scripts, see
`hosted/README.md`.

## The design folder

Every page takes the platform's look from one place: `design/` at the repository root, which
compose mounts read only at `/srv/design` in the Caddy container. Caddy serves exactly these paths
from it, as plain files, and nothing else:

- `/design/tokens.css`, the values
- `/design/fonts.css` and `/design/fonts/*`, the fonts, hosted here so no page asks an outside host
  for one
- `/design/sprites/*`, the agent avatars
- `/design/nav.js`, `/design/theme.js` and `/design/page.css`, the shared nav bar, the script that
  applies a remembered theme before the page paints, and the page shell around the bar with the
  page container; see
  "Navigation" in `High-level/docs/platform-architecture.md`
- `/design/gallery/*`, the component gallery, from `design/ui/gallery/dist`, which
  `npm run build` in `design/ui` writes and git ignores; until it is built this answers `404`, and
  `/design/gallery` redirects to `/design/gallery/`. See `design/ui/README.md`

Every other path under `/design/` answers `404` from Caddy, `DESIGN.md` and `reference/` among
them, since those are for the agents building pages and not for the browser. A slice that needs
another file served adds its path to the `@design` matcher in the Caddyfile. Each response carries
`Cache-Control: no-cache` with an `ETag`, so a browser revalidates on every load and sees a change
to the look at once, while an unchanged file costs a `304`.

A change to a file under `design/` is served at once, since the folder is mounted. A change to the
Caddyfile, a new path for instance, needs Caddy recreated, as above.

## The feature registry

`features.toml` lists every feature the core knows about: its name, the path prefix the Caddyfile
routes to it, the upstream the core calls it at directly, its display endpoint if it has one,
the name of its icon in the nav if it has one, which `design/nav.js` must hold, see "Navigation" in
`High-level/docs/platform-architecture.md`, and its attention endpoint if it has one, under its own
prefix, which the nav bar asks for the dot on its link, see "The attention endpoint" in
`High-level/docs/feature-contract.md`.
The core reads it once at startup, so a change needs a restart, and refuses to start if the file
is broken. Adding, moving or removing a feature changes `features.toml` and the Caddyfile in the
same commit, since the two describe the same routes, and `services.json` gives the port the feature
listens on; `scripts/ci/services.test.sh` checks that the three agree, see "The service manifest"
below. Why it is a file and not a table is in "The feature registry" in
`Core/docs/core-architecture.md`.

The brain's route has no entry, since the brain is a service and not a feature: the core neither
lists it in the nav nor asks it for front page data, see `Brain/README.md`.

## The service manifest

`services.json` describes every service the platform runs, once: one object, `{"services": [...]}`,
with one entry per service in start order.

| Field      | Holds                                                                                     |
| ---------- | ----------------------------------------------------------------------------------------- |
| `name`     | lowercase letters, digits and hyphens, unique, such as `brain`                             |
| `dir`      | its folder, relative to the repository root, such as `Brain`                               |
| `port`     | the port it listens on, an integer, unique                                                 |
| `health`   | optional, a path through Caddy that answers `200` without a token once it is up; a service without one is not waited for |
| `language` | `rust`, `python` or `dotnet`                                                                |
| `area`     | the area `scripts/ci/changed-areas.sh` prints for a change under `dir`; `dotnet` for a C# service, and only for one, which puts it in `dotnet_services` instead |
| `migrate`  | optional, the command run from `dir` to apply its migrations                               |
| `start`    | the command run from `dir` to start it in the foreground; it may use `$HOST`, 127.0.0.1 unless the caller sets it, and `$PORT`, the entry's port |
| `database` | optional: `name`, its `owner` and `app` roles, `owner_password` and `app_password`, the names of their variables in `.env`, `owner_url`, the variable `migrate` reads the owner's URL from, and optionally `app_url`, the one `start` reads the app's URL from |
| `ci`       | optional, for a C# service with a database: `{"init_script": "infra/postgres/initdb/<name>.sh"}`, the script `.github/workflows/dotnet.yml` creates its database and roles with |

Each `uv run` in a command has `UV_LOCKED=1` in front and each `cargo` command `--locked`, as
`scripts/ci/workflows-locked.test.sh` checks, since they also run outside CI's environment.

It is read through `scripts/agent/services.sh` alone, which lists the services and runs one's
`migrate` or `start` with its database URL built from `.env`, never printed. Its readers:

- `scripts/agent/reset-db.sh`, which recreates every database it lists;
- `scripts/ci/changed-areas.sh`, which maps each folder to its CI area, and sets every area when
  `services.json` itself changes, so a pull request cannot drop its own service to skip its checks;
  and lists each C# service to check in `dotnet_services`, with the fields `dotnet.yml` takes, so
  `ci.yml` runs `dotnet.yml` once for each;
- `.github/workflows/e2e.yml`, which migrates, starts and waits for every service;
- the agents' `pick-up-slice` skill, for its ports, password, migrate, start and health steps.

So a new service in a language the platform already has is an entry here, and its Dockerfile and
entries in `infra/hosted/`, which `agree.test.sh` holds it to, and nothing under `.github/`,
`scripts/` or `.claude/`. It is not part of `features.toml`: that is the core's
registry of features, and the brain, a service, is not one. It is JSON so that `jq` and GitHub
Actions can read it.

A feature changes `services.json`, `features.toml` and the Caddyfile in the same commit.
`scripts/ci/services.test.sh` checks that they agree, which nothing did before: every feature's
upstream port is a service's, the Caddyfile routes its prefix to that port, every port the
Caddyfile proxies to is a service's, and every database's password variables are in
`.env.example`. A new database also needs its init script in `postgres/initdb/`, see "The
databases" above.

The hosted copy repeats them, so a feature or a service also changes `hosted/compose.yaml`,
`hosted/features.toml`, `hosted/Caddyfile` and, for a new password, `hosted/.env.example`, with a
Dockerfile in the service's folder; `hosted/tests/agree.test.sh` checks those agree with the files
here. See `hosted/README.md`.

## The hosted copy

`hosted/` holds the platform built into images and started as one compose project of its own,
apart from this dev stack: its compose file, its Caddyfile and registry, and `check.sh`, which
proves on a throwaway copy that it builds and every service answers. The Dockerfiles are in each
service's folder. See `hosted/README.md`.

## Trusting Caddy's root

The browser accepts Caddy's certificates once the laptop trusts the root of Caddy's local
certificate authority. Only the root is trusted. The intermediate and the leaf are checked by
being signed up to it, and Caddy renews both on its own while it runs. It is done once per machine,
and again after every `docker compose down -v`, since that makes a new root.

Copy the root out of the volume to somewhere outside the repository, and look at what is about to
be trusted. From this directory, with Caddy running:

```zsh
docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt ~/caddy-root.crt
openssl x509 -in ~/caddy-root.crt -noout -subject -enddate -fingerprint -sha256
```

The subject is `Caddy Local Authority - <year> ECC Root`. The certificate is not secret, only the
private key beside it in the volume is, but it differs per machine and per volume, so it does not
belong in git.

Then trust it, which asks for the login password in a dialog:

```zsh
security add-trusted-cert -r trustRoot -p ssl -k ~/Library/Keychains/login.keychain-db ~/caddy-root.crt
```

A trusted root vouches for any certificate it signs, for any domain, and its private key sits in
a Docker volume, so the trust is kept as narrow as macOS allows. The login keychain rather than
System, so it applies to this user only and needs no sudo, which is enough since Safari and Chrome
read the user's trust settings. `-p ssl` trusts it for TLS servers only, not for code signing or
mail. Firefox keeps its own store and ignores the keychain unless `security.enterprise_roots.enabled`
is set.

To check, the fingerprint in the keychain matches the file, the trust setting says `SSL`, and both
routes answer without `-k`:

```zsh
security find-certificate -a -c "Caddy Local Authority" -Z ~/Library/Keychains/login.keychain-db | grep SHA-256
security dump-trust-settings | grep -A4 "Caddy Local Authority"
curl -sS -o /dev/null -w '%{http_code}\n' https://platform.test/
curl -sS -o /dev/null -w '%{http_code}\n' https://platform.test/canary/api/whoami
curl -sS -o /dev/null -w '%{http_code}\n' https://platform.test/brain/api/health
```

Without a session the first answers `303` to the core's refresh endpoint, which sends a request
with no session on to log in, and the second `401` from canary itself, an API route without a
token, and the third `200` from the brain, whose health needs no token; each code shows the
request crossed Caddy over a trusted chain, to the core, to canary and to the brain.
The browser shows a padlock on `https://platform.test/` and on `https://platform.test/canary`,
which sends a browser that is not logged in to the login page.

To remove a root, after `down -v` or for good, delete it by its SHA-1 from the `find-certificate`
output above:

```zsh
security delete-certificate -Z <sha1> ~/Library/Keychains/login.keychain-db
```

### After Caddy has been stopped for a while

The intermediate lives seven days and the leaf twelve hours, and Caddy renews them only while it
runs. Started again after more than about two weeks, it renews the intermediate but fails on the
leaf: it deletes the expired leaf from storage and then tries to renew from the deleted key, logging
`will retry` with `platform.test.key: no such file or directory`, while serving the old expired
chain. A browser says the certificate has expired, and `openssl s_client` returns verify code 10.
Restarting once fixes it, since with nothing in storage Caddy issues a new leaf:

```zsh
docker compose restart caddy
```
