# infra/hosted

The hosted copy of the platform: every service built into an image and started as one compose
project of its own, apart from the dev stack in `infra/compose.yaml`. It is the copy that stays up
on the Mac mini, reached over Tailscale. Development still runs natively on a laptop, see
"Databases, migrations, and running it" in `High-level/docs/platform-architecture.md`.

This folder makes the containers, proves they start, runs the acceptance suite against them, and
backs up every database each night, see "Backups". Releasing to the real copy arrives in a later
slice, as does running `check.sh` in CI.

## What runs

| Service    | Image                       | Listens on | Reached                                  |
| ---------- | --------------------------- | ---------- | ---------------------------------------- |
| `postgres` | `postgres:18`               | 5432       | by the services, on the project network  |
| `core`     | `platform-hosted/core`      | 8080       | through the proxy                        |
| `canary`   | `platform-hosted/canary`    | 8081       | through the proxy                        |
| `brain`    | `platform-hosted/brain`     | 8082       | through the proxy                        |
| `review`   | `platform-hosted/review`    | 8083       | through the proxy                        |
| `proxy`    | `platform-hosted/proxy`     | 80         | published, `127.0.0.1:9080` by default   |
| `backup`   | `platform-hosted/backup`    | nothing    | runs the nightly backup, see "Backups"   |

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
- `backup/`, the backup job's image and its bash scripts, and `postgres/backup-role.sh`, the role
  it dumps as, see "Backups".
- `check.sh`, with `front/`, its test HTTPS front, `tests/agree.test.sh`, and
  `tests/backup.test.sh` with its stand in bucket, `tests/s3_stub.py` and `tests/compose.yaml`, see
  below.

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
when the volume is first created, the same scripts as the dev stack, each mounted by name into the
init directory, and then `postgres/backup-role.sh`, last; each is safe to run again, see "The
databases" in `infra/README.md`. After the migrations, run the backup role's script again, so it
grants `CONNECT` on every database, the ones made since the volume was among them:

```zsh
docker compose -p <project> exec -T postgres bash /docker-entrypoint-initdb.d/zz-backup-role.sh
```

## Backups

The hosted copy shares a machine with agents that run unattended, so its backups are what has to
survive a dead disk and a bad command alike. Every night each database is dumped, encrypted to a
public key whose private half is never on the Mac mini, and uploaded to a bucket that cannot delete
what it holds for 30 days. The brain's own export stays a way to move one account's memory, see
"Export and import" in `Brain/README.md`; it is not the backup.

### What is backed up

Every database in the hosted Postgres except `postgres` and the templates, found by asking
Postgres at each run, so a database added later can never be forgotten. Nothing else: not the
volumes of the other services, which hold nothing of their own, and not the hosted `.env`.
Passwords can be set again, and a lost signing key only logs everyone out. Keep these outside the
machine, in a password manager: the `age` private key, which is the only way to read a backup, and
the bucket's details, its endpoint, region and name, so a new machine can find the files.

A backup succeeds only when every database was dumped and every file uploaded. Anything else makes
the run exit non zero, records the step that failed, and keeps the files it made.

### How a backup runs

The `backup` service runs `schedule`: it sleeps until the next `BACKUP_AT` in `TZ`, runs a backup,
and repeats. A failed night is recorded and the schedule goes on to the next. One backup runs at a
time; a second started meanwhile waits for the first.

1. It connects as `platform_backup` and lists the databases.
2. For each, `pg_dump --format=custom` is piped straight into `age -r "$BACKUP_AGE_RECIPIENT"`, so
   nothing unencrypted touches a disk, into the volume `backups` as
   `<UTC time as YYYYMMDDTHHMMSSZ>-<database>.dump.age`, the same time for every file of a run.
3. Each file is uploaded with one `PUT` by `curl`, to
   `$BACKUP_S3_ENDPOINT/$BACKUP_S3_BUCKET/$BACKUP_S3_PREFIX/<file name>`, signed with
   `--aws-sigv4` and carrying the `Content-MD5` a bucket with Object Lock requires. The key id and
   key reach `curl` as a config file on a file descriptor, never as arguments, so neither shows in
   a process list.
4. The status file is written, and local files dated, by their names, more than
   `BACKUP_KEEP_LOCAL_DAYS` days ago are removed. A failed run removes nothing.

The bucket's retention is the bucket's own rule, below. The job never deletes anything in the
bucket, and its key cannot.

**The status file** is `/status/backup.json` in the volume `backup_status`, written after every
attempt to a temporary file renamed over the old one, so it is never seen half written:

```json
{
  "last_attempt_at": "2026-11-15T02:00:04Z",
  "last_attempt_ok": true,
  "last_success_at": "2026-11-15T02:00:04Z",
  "last_error": null
}
```

Times are RFC 3339 in UTC, the attempt's start. `last_error` is `dump`, `encrypt` or `upload`,
the step that failed, never a command's output, whose detail is in the service's log.
`last_success_at` is `null` until the first success and survives a failed attempt. A run stopped
by a missing setting is not an attempt and writes nothing.

**Where the result shows.** The core has the volume mounted read only at `/status`, with
`CORE_BACKUP_STATUS_FILE=/status/backup.json`, and its front page ends with a line saying when the
last backup succeeded. The line turns amber when the last success is more than 26 hours old, the
last attempt failed, none has succeeded, or the file cannot be read; see "The backup line on the
front page" in `Core/docs/core-architecture.md`. Nothing alerts on a missed backup yet; that waits
for the notifications service.

**The backup role** is `platform_backup`, made by `postgres/backup-role.sh`: it logs in with
`BACKUP_DB_PASSWORD`, has `BYPASSRLS`, and is a member of `pg_read_all_data`, with `CONNECT` on
every database. `pg_dump` turns row level security off and fails on a table whose policy would hide
a row, and every scoped table here forces its policy even on its owner, so a dump needs a role that
bypasses it. It is the named bypass "Multi user, and how isolation is enforced" in
`High-level/docs/platform-architecture.md` asks for, and it reads everything and writes nothing.
The script runs when the volume is created and again after every migration, see "Running it by
hand", so a database added later gets its `CONNECT`.

### Settings

In `.env` beside the compose file, each listed in `.env.example`. The job checks them all when it
starts and stops, naming each one missing or malformed and never its value. There is no mode
without a bucket: a dump that never left the machine is not a backup.

| Setting                  | Default            | What it is                                                     |
| ------------------------ | ------------------ | -------------------------------------------------------------- |
| `BACKUP_DB_PASSWORD`     |                    | `platform_backup`'s password, also read by Postgres            |
| `BACKUP_AGE_RECIPIENT`   |                    | the `age` public key, `age1` followed by the rest              |
| `BACKUP_S3_ENDPOINT`     |                    | the bucket's endpoint as an origin, such as `https://s3.eu-north-1.amazonaws.com` |
| `BACKUP_S3_REGION`       |                    | its region, as the signature names it                          |
| `BACKUP_S3_BUCKET`       |                    | its name                                                       |
| `BACKUP_S3_KEY_ID`       |                    | the key id of a key restricted to the bucket                   |
| `BACKUP_S3_KEY`          |                    | that key                                                       |
| `BACKUP_S3_PREFIX`       | `platform`         | the folder in the bucket                                       |
| `BACKUP_AT`              | `03:00`            | the time of the nightly backup, `HH:MM`                        |
| `TZ`                     | `Europe/Stockholm` | the zone `BACKUP_AT` is in                                     |
| `BACKUP_KEEP_LOCAL_DAYS` | `7`                | days a dump stays in the local volume                          |

A time the change to summer time skips, such as `02:30` on the night clocks go from 02:00 to
03:00, runs at the same distance past the gap, `03:30`; a time the change back repeats runs once,
at its second occurrence. As for every variable, one exported in the shell running compose wins
over `.env`, `TZ` included.

### Setting up the bucket

Once, by the owner, at a provider that speaks S3 and supports Object Lock:

1. Create the bucket with **Object Lock** on, and a default retention of **30 days** in compliance
   mode, so nothing uploaded can be deleted or overwritten for 30 days, by anyone.
2. Add a **lifecycle rule** removing objects under the prefix 30 days after they were created,
   so the bucket keeps a month of nights.
3. Create a **key restricted to this bucket**, allowed to put objects and nothing more: no delete,
   no change to the bucket's rules. Its id and key are `BACKUP_S3_KEY_ID` and `BACKUP_S3_KEY`.
4. **Check the lock**: after the first backup, try to delete one of its files in the provider's
   console with an account that may. It must be refused.

### Making the age key pair

On another machine than the Mac mini, never on it, with `age` installed there:

```zsh
age-keygen -o backup-key.txt
```

It prints the public key, `age1…`, which goes into `BACKUP_AGE_RECIPIENT`. The file holds the
private key. Put it in the password manager and one offline copy, then delete the file. It must
never be on the Mac mini, in `.env`, in this repository or in the bucket: then neither a leaked
backup nor anything running on the machine can read one. Without it no backup can be restored.

### The first backup

With the settings in `.env` and the stack up, run one by hand, from this folder:

```zsh
docker compose -p <project> run --rm backup now
docker compose -p <project> run --rm --entrypoint cat backup /status/backup.json
```

It prints each database dumped and each file uploaded, and exits non zero if any step failed. The
stand in bucket of the tests checks no signature, so **this first real run is the check** that the
provider accepts `curl`'s signature and the `Content-MD5`: look in the bucket for one file per
database under the prefix, and do step 4 of the bucket's setup. A later slice calls `backup now`
at the start of every release.

### Restoring

`restore` decrypts one file with the private key given on standard input, and runs
`pg_restore --exit-on-error --single-transaction` as the superuser, so owners and grants come back
as they were, all of the dump or none of it. The superuser's password is given to that one run by
the person running it; the `backup` service's own environment never has it.

1. **Get the file.** A file still in the volume is named by its name alone. One from the bucket,
   downloaded from the provider's console, is mounted into the run, as in step 3.
2. **Choose the target.** By default the dump goes back into the database of its name. It must have
   no table in `public`, or the restore refuses and changes nothing. `--as <name>` restores beside
   it under another name, to look at an old backup next to the live one; `--replace` drops the
   target and creates it again first, for a live database that is broken. A target created this
   way is owned by the owner of the database it replaces, or of the database of the dump's name,
   with the same grants on it, so its app role connects as before. On a fresh volume the init
   scripts have made each service's database empty, so a plain restore of each is the way back.
3. **Run it**, from this folder, with the superuser's password in the shell and the private key on
   standard input. Copy the key from the password manager and pipe it from the clipboard, so it
   never lands in a file on the machine:

   ```zsh
   export POSTGRES_PASSWORD="$(grep '^POSTGRES_PASSWORD=' .env | cut -d= -f2-)"
   pbpaste | docker compose -p <project> run --rm -T -e POSTGRES_PASSWORD backup \
     restore brain 20261115T020004Z-brain.dump.age --as brain_old
   ```

   Then copy something else over the clipboard, and `unset POSTGRES_PASSWORD`. `-T` is needed for
   the key to arrive on standard input. For a file from the bucket, add
   `-v "$PWD/20261115T020004Z-brain.dump.age:/restore/file.age:ro"` before `backup`, and give
   `/restore/file.age` as the file.
4. **Check it** before pointing anything at it: the tables and their row counts, as the superuser.
   A restore under `--as` is dropped when you are done with it,
   `docker compose -p <project> exec -T postgres psql -U postgres -c 'DROP DATABASE brain_old'`.

Before anything is dropped, `restore` checks that the file decrypts in full with the key and holds
a `pg_dump` archive. `age` reads the key from standard input once; the script holds it in memory
and hands it to `age` on a pipe, so it is never written to a file inside the container.

### Testing the backups

`tests/backup.test.sh` runs from `check.sh` against its throwaway project, never `platform-hosted`,
which it refuses. It makes its own `age` key pair in the backup image and removes it at exit,
creates a database `probe` with a table under forced row level security holding rows of two users,
and checks: every database is dumped, encrypted and uploaded byte for byte with a signed request
and a matching `Content-MD5`; every database restores under another name with the same tables,
row counts, row hashes in primary key order, owners, grants and policies, and the app role reads
the copy through its policy; a non empty target is refused without `--replace`; a failed upload is
a failed backup with the status saying so; local retention; each setting missing or malformed; no
secret in the output, the status or any program's arguments; the backup role reads and cannot
write; and the schedule's sleep around `BACKUP_AT` and across both daylight saving changes.

Its bucket is `tests/s3_stub.py`, Python's standard library only, run as `s3stub` on
`python:3.14-slim-trixie` and published nowhere. It stores each `PUT`, refuses one without an
`AWS4-HMAC-SHA256` authorization or with a `Content-MD5` that does not match, and refuses
everything while told to. It does not verify the signature; the first real backup is the check
of that.

## check.sh

`check.sh` proves the stack works, on a throwaway copy. From the repository root, with Docker
running, and `uv` and Playwright's Chromium installed for the acceptance suite, which it never
installs (from `e2e/`, `uv run playwright install chromium`), and `platform.test` resolving to
`127.0.0.1`, as for the dev suite:

```zsh
bash infra/hosted/check.sh
```

It runs `tests/agree.test.sh` first, then builds every image, starts the project `hostedcheck`
with passwords, a signing key and backup settings made for this run in a temporary env file, waits
for Postgres, runs every `*-migrate` service and the backup role's script, starts the rest, the
test front and the stand in bucket, and waits up to 180 seconds
for each health path `scripts/agent/services.sh health` lists to answer `200`. The test front,
`front/`, is `caddy:2` with `tls internal`, serving `https://platform.test:9443` on
`127.0.0.1:9443` and proxying to `proxy:80`, standing in for `tailscale serve`; its file is added
with a second `-f` and is no part of the hosted copy. The stand in bucket, `s3stub`, is added the
same way, from `tests/compose.yaml`, see "Testing the backups". Its images are tagged `check`, so it never
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
- `the_acceptance_suite_passes_against_the_containers`: one `backup now`, so the status file the
  front page's backup line reads exists, then the whole suite in `e2e/`, every test but
  those marked `dev_stack_only`, passes against `https://platform.test:9443`, with invite codes and
  reset tokens made in the core's container through `E2E_CORE_COMMAND`, and the status file read
  and replaced in the backup's container through `E2E_BACKUP_STATUS_READ` and
  `E2E_BACKUP_STATUS_WRITE`, see "Fixtures" in `e2e/README.md`. Its output is shown as it
  runs, and it says so when `uv` or Chromium is missing. A test that fails here because of the
  hosted shape is fixed in the image or the compose file, never marked; see "Markers" in
  `e2e/README.md`. Its screenshots land in `e2e/screenshots/` and are not published;
- `backup_test`, `tests/backup.test.sh` passes, after the suite so the databases it backs up hold
  the suite's data, see "Testing the backups";
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
reads is in `.env.example`, every install in a Dockerfile is locked, the compose file names no
project, and each script in `infra/postgres/initdb/` is mounted into the hosted Postgres with the
backup role's last. Each check also runs on a broken copy, so it cannot pass by matching nothing.

Neither runs in CI yet; a later slice adds that.

The suite runs after the cases above, and takes as long as a dev run of it, several minutes, on top
of the build; the backup tests take about a minute more.

## Resources

Building every image at once needs more memory than Docker Desktop's default; the Mac mini's is
raised to 8 GB. The first build fetches every crate and package, and takes several minutes; later
builds reuse Docker's cache.
