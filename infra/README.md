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
kept, and the rest to the core. Why Caddy, rather than Traefik or nginx, is in "Routing" in
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
