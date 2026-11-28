# infra/hosted

The hosted copy of the platform: every service built into an image and started as one compose
project of its own, apart from the dev stack in `infra/compose.yaml`. It is the copy that stays up
on the Mac mini, reached over Tailscale. Development still runs natively on a laptop, see
"Databases, migrations, and running it" in `High-level/docs/platform-architecture.md`.

This folder makes the containers, proves they start, runs the acceptance suite against them, and
backs up every database each night, see "Backups". The owner sets the real copy up once, see
"Setting it up", and releases a version into it with one command, see "Releasing". CI runs
`check.sh` on a pull request that changes what the images are built from, see "In CI".

## What runs

| Service    | Image                       | Listens on | Reached                                  |
| ---------- | --------------------------- | ---------- | ---------------------------------------- |
| `postgres` | `postgres:18`               | 5432       | by the services, on the project network  |
| `core`     | `platform-hosted/core`      | 8080       | through the proxy                        |
| `canary`   | `platform-hosted/canary`    | 8081       | through the proxy                        |
| `brain`    | `platform-hosted/brain`     | 8082       | through the proxy                        |
| `review`   | `platform-hosted/review`    | 8083       | through the proxy                        |
| `devflow`  | `platform-hosted/devflow`   | 8084       | through the proxy                        |
| `proxy`    | `platform-hosted/proxy`     | 80         | published, `127.0.0.1:9080` by default   |
| `backup`   | `platform-hosted/backup`    | nothing    | runs the nightly backup, see "Backups"   |
| `devflow-worker` | `platform-hosted/devflow-worker` | nothing | built, never started              |

`devflow-worker` is the image Dev flow's workers are built FROM, and has no replica: the release's
`docker compose build` builds it at the release's tag beside the rest, and `docker compose up`
starts nothing of it. A worker is built from it and started by the command on a project's page,
outside this project, see "Starting a worker" in `devflow/README.md`; Dev flow is given its name as
`DEVFLOW_WORKER_IMAGE`, so a worker on an older one is told to rebuild.

Two more of Dev flow's settings come from this copy rather than its `.env`'s own lines, see
"Machines" in `devflow/README.md`. `DEVFLOW_LOCAL_URL` is `http://${HOSTED_HTTP_BIND:-127.0.0.1:9080}`,
the proxy's published address, which the second command of "Add a machine" names for the machine
this copy runs on, since that machine cannot reach its own public origin. `DEVFLOW_RELEASE` is
`${PLATFORM_RELEASE:-dev}`, the release's tag, which a machine's `hello` answers so its program can
update itself.

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
  with `-p`. The real copy's, `platform-hosted`, is passed by `scripts/deploy/release.sh`, and
  `check.sh` passes `hostedcheck`. A compose command typed here without `-p` gets Compose's default, the
  folder's name, `hosted`, which is neither, so a command typed in another clone never reaches
  the real copy by accident.
- `Caddyfile`, the proxy's: the dev Caddyfile's routes on one plain HTTP site, `:80`, with each
  upstream the service's compose name, `canary:8081`, `review:8083`, `devflow:8084`, `brain:8082`
  and the rest `core:8080`, and the same design paths, served from files copied into the proxy's
  image.
- `features.toml`, the core's registry in the hosted copy: `infra/features.toml` feature for
  feature, with each upstream `http://<service>:<port>`. Two files rather than one with
  placeholders, since the core reads a plain file; `tests/agree.test.sh` holds them together.
- `.env.example`, every variable `compose.yaml` reads, each with what it is. The real copy's
  values go in `.env` beside it, which git ignores.
- `proxy/Dockerfile`, the proxy's image. Each service's Dockerfile is in its own folder:
  `Core/Dockerfile`, `canary/Dockerfile`, `Brain/Dockerfile`, `review/Dockerfile` and
  `devflow/Dockerfile`.
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

Dev flow's final stage also holds git, from Debian's packages, for its fake code host alone: in the
fake mode it keeps a bare git repository per repository, so `check.sh`'s suite can build a card.
The real code host never runs git there.

The worker's base image, `devflow/worker.Dockerfile`, is built and never started. Its build stage
publishes the worker program from the committed `packages.lock.json` files, and its final stage
installs Claude Code and Playwright with `npm ci` from `devflow/worker/package-lock.json`, with
Playwright's Chromium, so it is held to the same rules, which `agree.test.sh` checks. It is the one image whose final stage runs as root: the
worker program keeps a job's secrets from the agent, which runs as `agent`, uid 1000, see "The
base image" in `devflow/README.md`. Since no container of it runs here, `no_container_runs_as_root`
leaves it out, and `devflow/tests/image/base-image.test.sh` checks it instead.

## Setting it up

Once, by the owner, over SSH on the Mac mini unless a step says otherwise. Every command is written
out in full for zsh. The real copy's project is always `platform-hosted`, its clone `~/platform`
and its settings `infra/hosted/.env` in that clone.

1. **Docker's memory.** In Docker Desktop's settings, Resources, set the memory to 8 GB, see
   "Resources".
2. **The bucket and the key pair.** Set the bucket up as "Setting up the bucket" says: versioning
   and Object Lock with a default retention of 30 days in governance mode, the lifecycle rule for
   current and noncurrent versions, and the restricted key. Make the `age` key pair on another
   machine, as "Making the age key pair" says: its public key goes into `.env` in step 3, and its
   private key never comes onto the Mac mini. Step 4 of the bucket's setup, checking that an
   uploaded file cannot be deleted, waits for the first upload, in step 8.
3. **The clone and its settings.** The real copy is released from a clone of its own, never from
   `~/dev/Personal-platform`, where the runner checks out slice branches. The release script does
   not check the path; this step is what keeps them apart.

   ```zsh
   git clone https://github.com/Adamniels/Personal-platform.git ~/platform
   cd ~/platform
   cp infra/hosted/.env.example infra/hosted/.env
   chmod 600 infra/hosted/.env
   openssl rand -hex 24
   ```

   Fill in each value in `infra/hosted/.env` as its comment says: each of the ten passwords from
   its own run of `openssl rand -hex 24`, Dev flow's vault key, `DEVFLOW_VAULT_KEY`, from
   `printf 'k1:%s\n' "$(openssl rand -base64 32)"` and saved in the password manager at once,
   Dev flow's code host, `DEVFLOW_CODE_HOST`, left as `github`, Dev flow's GitHub App, its
   `DEVFLOW_GITHUB_CLIENT_ID`, `DEVFLOW_GITHUB_CLIENT_SECRET` and `DEVFLOW_GITHUB_APP_SLUG` from the
   app's page on GitHub and `DEVFLOW_GITHUB_PRIVATE_KEY_FILE`, the path on the Mac mini of the
   private key that page makes, `PLATFORM_PUBLIC_ORIGIN` from step 4, and the backup settings from step 2.
   The key file stays outside the clone, for example `chmod 600` in `~/.config/platform/`, and
   in the password manager; `compose.yaml` mounts it read only into Dev flow's container at
   `/run/secrets/github-app.pem`, and a path that names no file stops compose before anything
   starts. Register the app first, as "Registering the GitHub App" in `devflow/README.md` says; its
   homepage and callback are the origin from step 4, so step 4 comes before it. Then make the
   signing key in the core's own image, which appends its line to the file:

   ```zsh
   docker compose -p platform-hosted --env-file infra/hosted/.env -f infra/hosted/compose.yaml build core
   docker compose -p platform-hosted --env-file infra/hosted/.env -f infra/hosted/compose.yaml run --rm --no-deps -T core keygen >> infra/hosted/.env
   ```

   The key is made for this copy alone, never copied from another environment, see "The signing
   key" in `Core/docs/core-architecture.md`.
4. **Tailscale.** In Tailscale's admin console: turn on MagicDNS and HTTPS certificates, under DNS,
   and disable key expiry for the Mac mini, under Machines, so it never drops off the tailnet by
   itself. `PLATFORM_PUBLIC_ORIGIN` is then `https://<machine>.<tailnet>.ts.net`, the machine's
   name and the tailnet's DNS name as the console shows them, with no port.
5. **The first release**, from `~/platform`:

   ```zsh
   bash scripts/deploy/release.sh
   ```

   It takes several minutes the first time; "Releasing" says what it does and what each failure
   means. It ends by printing the commit and each service running.
6. **HTTPS inside the tailnet.** Share the proxy, plain HTTP on `127.0.0.1:9080`, over HTTPS to the
   tailnet alone:

   ```zsh
   tailscale serve --bg 9080
   tailscale serve status
   ```

   If the installed version documents another form for sharing `http://127.0.0.1:9080`, use that;
   `tailscale serve --help` says. With the Mac app, the command is not on the `PATH` over SSH
   unless its command line was installed; the app's own is
   `/Applications/Tailscale.app/Contents/MacOS/Tailscale`, which takes the same arguments. The
   setting survives a restart. **Never `tailscale funnel`**, which would put the platform on the
   public internet. Then open `PLATFORM_PUBLIC_ORIGIN` on the phone, with Tailscale on, and check
   the login page loads with a valid certificate.
7. **An account, and the memory.** Make an invite code in the core's container, and register with
   it from the phone or the laptop:

   ```zsh
   docker compose -p platform-hosted --env-file infra/hosted/.env -f infra/hosted/compose.yaml exec -T core platform_core invite
   ```

   Then move the brain's memory from the laptop. Export it there, as "Export and import" in
   `Brain/README.md` says, and copy the file into `~/platform` over SSH as `memory.json`. Find the
   new account's id, and import the file under it:

   ```zsh
   docker compose -p platform-hosted --env-file infra/hosted/.env -f infra/hosted/compose.yaml exec -T postgres psql -U postgres -d core -c 'SELECT id, email FROM accounts'
   PLATFORM_RELEASE="$(head -n 1 infra/hosted/.release | cut -c 1-12)" docker compose -p platform-hosted --env-file infra/hosted/.env -f infra/hosted/compose.yaml run --rm -T -v "$PWD/memory.json:/import/memory.json:ro" brain brain import /import/memory.json --as <account-id>
   rm memory.json
   ```

   The file is the whole memory in plain text, so it is removed from both machines once imported,
   and the clone must hold no untracked file at the next release anyway. A `run` after a release
   names the release's tag, the first 12 characters of the commit in `infra/hosted/.release`, as
   above: without it compose looks for the `dev` image, says pull access is denied, and builds one
   from the clone. An `exec` uses the running container and needs no tag.
8. **A backup, and the bucket.** The release took one already; take another by hand and look:

   ```zsh
   PLATFORM_RELEASE="$(head -n 1 infra/hosted/.release | cut -c 1-12)" docker compose -p platform-hosted --env-file infra/hosted/.env -f infra/hosted/compose.yaml run --rm backup now
   ```

   Look in the bucket for one file per database under the prefix, and do step 4 of "Setting up the
   bucket": deleting a version of one of them must be refused.

## Releasing

From `~/platform`, over SSH:

```zsh
bash scripts/deploy/release.sh
```

releases `origin/main` as it is after a `git fetch`. To release an older commit again, such as the
one before a bad release, name it. It must be on `main`, so a branch is never released:

```zsh
cat infra/hosted/.release
bash scripts/deploy/release.sh <commit>
```

A release is the owner's command and never happens on a merge, see "The hosted copy is released by
hand" in `High-level/docs/platform-architecture.md`. `scripts/deploy/release.sh` prints each step
as it starts:

1. **Preflight.** Docker answers; `infra/hosted/.env` exists, and every variable `.env.example`
   sets, and `CORE_SIGNING_KEY`, is set and not empty in it; each password is letters and digits
   only; `PLATFORM_PUBLIC_ORIGIN` begins with `https://`; and the clone has no changes, untracked
   files included. The env file is the only source of settings: a variable exported in the shell
   is ignored, unlike for a compose command typed by hand. No value is ever printed or put on a
   command line.
2. **Fetch and choose.** `git fetch origin`, then the commit, which must be `origin/main` or before
   it, checked out detached.
3. **Build** every image as `platform-hosted/<service>:<the commit's first 12 characters>`.
4. **Postgres.** Started, then every script in `infra/postgres/initdb/` and the backup role's run
   inside it. Each is safe to run again, so a service added in this release gets its database and
   roles here, with no step by hand. Its two passwords must be in `infra/hosted/.env` first, or
   step 1 refuses: Dev flow's, `DEVFLOW_OWNER_PASSWORD` and `DEVFLOW_APP_PASSWORD`, came with its
   database, so a copy set up before them refuses every release until both are added, each from
   its own run of `openssl rand -hex 24`. So does Dev flow's code host, `DEVFLOW_CODE_HOST=github`,
   which came with the code host's slice: the hosted copy names its mode, since Dev flow's default
   elsewhere is the fake, which must never be on where real data is. With `github`, the GitHub
   App's four settings are required too, see step 3 of "Setting it up".
5. **Back up**, with `backup now`. Its time names the backup in any later message.
6. **Migrate**, each `<name>-migrate` service in the compose file's order, one at a time.
7. **Start** the new containers, `docker compose up -d`, which replaces those whose image or
   settings changed.
8. **Health.** Each health path `scripts/agent/services.sh health` lists answers `200` through the
   proxy's published address, within 180 seconds.
9. **Record** the commit in `infra/hosted/.release`, git ignored, print what is running, and remove
   the images of releases older than the previous one, so a rollback always has its images.

What each failure means:

- **Before step 7**, a refused preflight, a commit not on `main`, or a failed build, Postgres,
  backup or migration: nothing was started or stopped, the previous release is still serving, and
  the clone is checked out where it was. A failed migration says which `-migrate` service failed
  and which ran before it, since those are not undone, and names the backup step 5 took.
- **`rolled back`**: the new containers did not answer and no file under any `migrations/` folder
  changed since the recorded release, so its images were started again and its commit checked out
  again. Nothing is recorded; read the failed release's logs, below, to find why.
- **`the database is ahead of the previous version`**: the new containers did not answer, and a
  migration changed since the recorded release. Migrations only go forward and the old code may
  not fit the new schema, so nothing is rolled back and the new containers are left up. Read their
  logs. Either release a fix, or go back: restore each database from the backup the message names,
  with `--replace`, as "Restoring" says, then release the recorded commit again.
- **`nothing to roll back to`**: the first release did not answer. The containers are left up for
  their logs.

Logs, for any service:

```zsh
docker compose -p platform-hosted --env-file infra/hosted/.env -f infra/hosted/compose.yaml logs --tail 100 core
```

`release.sh --local` builds the tree as it is under another project, for `check.sh`. It is refused
for `platform-hosted`, tags its images with the env file's `PLATFORM_RELEASE` or `local` rather than
a commit, and records, rolls back and removes nothing.

## After a reboot

Nothing to do. Docker Desktop starts at login, set in its settings under General, and every service
has `restart: unless-stopped`, so the containers come back by themselves, with the release they
were running. To check, from `~/platform`:

```zsh
docker compose -p platform-hosted --env-file infra/hosted/.env -f infra/hosted/compose.yaml ps
```

Docker Desktop starts only once the Mac mini's user is logged in, so after a power cut the copy is
down until someone logs in there, unless automatic login is on, which FileVault does not allow.

## What to keep elsewhere

The `age` private key, in the password manager and one offline copy, and the bucket's account, its
endpoint, region and name, so a new machine can find the backups. And Dev flow's vault key,
`DEVFLOW_VAULT_KEY` from `.env`, in the password manager: the backups hold Dev flow's secrets sealed
with it and never the key, so without it they cannot be opened and each is entered again, a GitHub
link by linking again, see "The vault" in `devflow/README.md`. Nothing else: the passwords and the
signing key in `.env` are made again on a new machine, which only logs everyone out, see "What is
backed up", and Dev flow's GitHub App's private key and client secret are made again on the app's
page on GitHub, which leaves the old ones to revoke there.

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
machine, in a password manager: the `age` private key, which is the only way to read a backup, the
bucket's details, its endpoint, region and name, so a new machine can find the files, and Dev
flow's vault key, without which the secrets in its backed up database stay sealed.

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
The script runs when the volume is created and again in every release, after the init scripts,
see "Releasing", so a database added later gets its `CONNECT`.

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

1. Create the bucket with versioning and **Object Lock** on, and a default retention of **30 days**
   in **governance** mode, as a fixed retention rather than an event hold, which would keep every
   file until someone lifts the hold. Nothing uploaded can then be deleted or overwritten for 30
   days by the job's key or by the lifecycle rule; only an account allowed to bypass governance
   retention can, which is the owner's login at the provider and never the key on the Mac mini.
   Compliance mode would refuse the owner too. Governance is chosen so that the owner can still
   remove what a leaked key uploaded, at the cost of trusting the provider account, which has MFA
   and is not used day to day.
2. Add a **lifecycle rule** for the prefix that expires current versions 30 days after they were
   created and permanently deletes noncurrent versions 1 day after they become noncurrent, so the
   bucket keeps a month of nights. Object Lock needs versioning, and in a versioned bucket an
   expiry only adds a delete marker, so without the second action nothing is ever removed and the
   bucket grows every night. A locked version is removed only once its retention has passed, which
   on day 31 it has.
3. Create a **key restricted to this bucket**, allowed to put objects under the prefix and nothing
   more: no delete, no list, no change to the bucket's rules and no bypass of its retention. The
   job makes one `PUT` per file and sends no lock headers of its own, so on AWS this is an IAM user
   without console access whose one inline policy is:

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Effect": "Allow",
         "Action": "s3:PutObject",
         "Resource": "arn:aws:s3:::<bucket>/platform/*"
       }
     ]
   }
   ```

   Its access key id and secret are `BACKUP_S3_KEY_ID` and `BACKUP_S3_KEY`.
4. **Check the lock**: after the first backup, in the provider's console with an account that may
   delete, show the bucket's versions and delete one version of one of its files, without
   bypassing governance retention. It must be refused. Deleting a file without choosing a version
   only adds a delete marker, which the lock allows, so it proves nothing.

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

In the hosted copy each `run` names the release's tag, as step 8 of "Setting it up" does.

It prints each database dumped and each file uploaded, and exits non zero if any step failed. A
refused upload shows only its status, such as `403`, since `curl --fail` keeps the provider's
reply out of the log. The likeliest cause on a first run is a value pasted by hand that lost a
character: an AWS key id is 20 characters and starts with `AKIA`, and its secret is 40. The
stand in bucket of the tests checks no signature, so **this first real run is the check** that the
provider accepts `curl`'s signature and the `Content-MD5`: look in the bucket for one file per
database under the prefix, and do step 4 of the bucket's setup. Every release runs `backup now`
before it migrates, see "Releasing".

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
running, and `uv`, Playwright's Chromium and `design/ui`'s packages installed for the acceptance
suite, which it never installs (from `e2e/`, `uv run playwright install chromium`, and from
`design/ui/`, `npm ci`, for the `axe-core` its accessibility checks inject), and `platform.test`
resolving to `127.0.0.1`, as for the dev suite:

```zsh
bash infra/hosted/check.sh
```

It runs `tests/agree.test.sh` first, then makes passwords, a signing key and backup settings for
this run in a temporary env file, building the core's and the backup's images to make the last two,
and starts the stand in bucket. Then it starts the project `hostedcheck` the way the real copy is
released, with `scripts/deploy/release.sh --local`: every image built, Postgres and its scripts, a
backup, every `*-migrate` service, the rest started and each health path answering through the
proxy, see "Releasing". It starts the test front last, and waits up to 180 seconds for each health
path `scripts/agent/services.sh health` lists to answer `200` through it. The test front,
`front/`, is `caddy:2` with `tls internal`, serving `https://platform.test:9443` on
`127.0.0.1:9443` and proxying to `proxy:80`, standing in for `tailscale serve`; its file is added
with a second `-f` and is no part of the hosted copy. The stand in bucket, `s3stub`, is added the
same way, from `tests/compose.yaml`, see "Testing the backups". Its images are tagged `check`, so it never
retags one the real copy runs.

It prints `ok <name>` or `FAIL <name>` for each case and exits non zero if any failed:

- `agree_test`, `tests/agree.test.sh` passes;
- `it_refuses_the_real_project`: with `HOSTED_PROJECT=platform-hosted` it exits non zero before
  running any docker command;
- `a_release_brings_the_stack_up`: `release.sh --local` exits `0` on this copy, so every step of a
  release passed, its own health check through the proxy among them;
- `the_stack_builds_and_every_service_answers`: the build, every migration, every health path
  answering `200` through the front, and `/canary/api/whoami` answering `401`;
- `the_design_files_and_the_gallery_are_served`: `/design/tokens.css` and `/design/gallery/`
  answer `200`, and `/design/DESIGN.md` answers `404`;
- `nothing_but_the_proxy_is_published`: of the project's containers only the proxy and the front
  publish a port, both on `127.0.0.1`;
- `the_worker_base_image_is_built_and_never_started`: the release built `devflow-worker`'s image,
  and no container of it exists;
- `no_container_runs_as_root`: every process of each service in `compose.yaml` that has replicas
  runs as a user id other than 0. The front, which is not one of them, runs as Caddy's image does,
  and `devflow-worker`, which has none, is never started;
- `the_acceptance_suite_passes_against_the_containers`: one `backup now`, so the status file the
  front page's backup line reads exists, then the whole suite in `e2e/`, every test but
  those marked `dev_stack_only`, passes against `https://platform.test:9443`, with invite codes and
  reset tokens made in the core's container through `E2E_CORE_COMMAND`, and the status file read
  and replaced in the backup's container through `E2E_BACKUP_STATUS_READ` and
  `E2E_BACKUP_STATUS_WRITE`, see "Fixtures" in `e2e/README.md`. Its output is shown as it
  runs, and it says so when `uv` or Chromium is missing. A test that fails here because of the
  hosted shape is fixed in the image or the compose file, never marked; see "Markers" in
  `e2e/README.md`. Its screenshots land in `e2e/screenshots/` and are not published;
- `base_image_test`, `devflow/tests/image/base-image.test.sh` passes: it builds the worker's base
  image under a tag of its own, from the cache the release's build warmed, and checks the agent
  cannot read the worker's files and that the worker, Claude Code, git and node run;
- `backup_test`, `tests/backup.test.sh` passes, after the suite so the databases it backs up hold
  the suite's data, see "Testing the backups";
- `it_leaves_nothing_behind`: after the run, and after a run made to fail once Postgres is up, no
  container, network or volume of the project exists;
- `it_saves_the_logs_of_a_failed_run`: that failed run wrote Postgres's log and its container's
  state to a directory before its teardown, as a failed case does to `HOSTED_CHECK_LOGS`.

With `HOSTED_CHECK_LOGS` naming a directory, a run in which any case failed writes the project's
`docker compose ps -a`, as `ps.log`, and `docker compose logs`, as `compose.log`, there before the
teardown removes the containers. CI's hosted job sets it and uploads the directory, see "In CI".

It always ends with `docker compose down -v` for its project, by a trap, and removes nothing else:
images and Docker's build cache stay, so a second run builds only what changed. `HOSTED_PROJECT`
names another project, for two runs side by side. It works while the dev stack is up, since it
uses neither its ports, 80, 443 and 5432, nor its project. Later slices add their cases to it.

`tests/agree.test.sh` reads files only and needs no Docker, in the style of
`scripts/ci/services.test.sh`: each manifest entry is a compose service on its port with its
`-migrate` service, no serving service has an owner's URL, the hosted registry and Caddyfile match
the dev ones, the proxy's image holds every design path it serves, every variable the compose file
reads is in `.env.example`, every install in a Dockerfile, `devflow/worker.Dockerfile` among them,
is locked, by the rules in
`scripts/ci/dockerfile.sh`, which `scripts/ci/workflows-locked.test.sh` also holds every Dockerfile
to on every pull request, the compose file names no
project, and `platform-hosted` is in it only as an image's name, a service's or Dev flow's
`DEVFLOW_WORKER_IMAGE`, and each script in `infra/postgres/initdb/` is mounted into the hosted Postgres with the
backup role's last. Each check also runs on a broken copy, so it cannot pass by matching nothing.

The suite runs after the cases above, and takes as long as a dev run of it, several minutes, on top
of the build; the backup tests take about a minute more.

### In CI

`.github/workflows/hosted.yml` runs `check.sh` on GitHub's runner, called by `ci.yml` as the job
`hosted` whenever `scripts/ci/changed-areas.sh` prints `hosted=true`, and `ci-ok` fails when it
fails. That is a change under `infra/hosted/`, `scripts/deploy/`, `infra/postgres/` or
`.github/workflows/`, to `infra/services.json`, to any `Dockerfile` or `.dockerignore`, or to any
dependency manifest, lockfile or toolchain pin: `Cargo.toml`, `Cargo.lock`, `rust-toolchain.toml`,
`pyproject.toml`, `uv.lock`, `.python-version`, `package.json`, `package-lock.json`,
`.node-version`, `*.csproj`, `Directory.Packages.props`, `Directory.Build.props`,
`packages.lock.json` and `global.json`. Source code alone does not run it, since building every
image adds minutes to a queue the runner waits on.

What that leaves to a release, knowingly: a source change that breaks an image without touching
any of those, such as a new file a service reads at runtime that its Dockerfile does not copy. The
release's build or health check fails, and the release changes nothing, see "Releasing". Running the
check on every pull request waits for the runner's retirement, as the hosting feature records.

The job points `platform.test` at the runner, installs uv, Playwright's Chromium and `design/ui`'s
packages for the suite, and no Rust or .NET, since the images bring their own toolchains. It has no
Docker layer cache, so each run builds every image from nothing, within its 45 minute timeout. On a
failure it uploads `HOSTED_CHECK_LOGS` as the artifact `hosted-logs`.

GitHub's runner is `linux/amd64` and the Mac mini `linux/arm64`, so the same Dockerfiles build on
both: CI checks the first, and the implementer's own run of `check.sh` on the Mac mini, the second.

## Resources

Building every image at once needs more memory than Docker Desktop's default; the Mac mini's is
raised to 8 GB. The first build fetches every crate and package, and takes several minutes; later
builds reuse Docker's cache.
