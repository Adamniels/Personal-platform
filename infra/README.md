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
