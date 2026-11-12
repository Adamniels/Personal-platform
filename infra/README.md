# infra

What the platform runs on locally: Postgres and Caddy in `compose.yaml`, and the Caddyfile that
routes the hostname to the core and the features. Why it runs under a real hostname with real
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

Caddy serves `https://platform.test`, routing `/canary` and everything under it to canary, prefix
kept, serving a few files of the design folder itself under `/design/` (see "The design folder"
below), and routing the rest to the core. Why Caddy, rather than Traefik or nginx, is in "Routing" in
`Core/docs/core-architecture.md`. The core and canary run natively, on 8080 and 8081, and Caddy
runs in compose. From this directory:

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

## The design folder

Every page takes the platform's look from one place: `design/` at the repository root, which
compose mounts read only at `/srv/design` in the Caddy container. Caddy serves exactly these paths
from it, as plain files, and nothing else:

- `/design/tokens.css`, the values
- `/design/fonts.css` and `/design/fonts/*`, the fonts, hosted here so no page asks an outside host
  for one
- `/design/sprites/*`, the agent avatars
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
routes to it, the upstream the core calls it at directly, and its display endpoint if it has one.
The core reads it once at startup, so a change needs a restart, and refuses to start if the file
is broken. Adding, moving or removing a feature changes `features.toml` and the Caddyfile in the
same commit, since the two describe the same routes and nothing checks that they agree. Why it is
a file and not a table is in "The feature registry" in `Core/docs/core-architecture.md`.

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
```

Without a session the first answers `303` to the core's refresh endpoint, which sends a request
with no session on to log in, and the second `401` from canary itself, an API route without a
token; either code shows the request crossed Caddy over a trusted chain, to the core and to canary.
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
