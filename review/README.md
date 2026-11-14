# review

Review is a queue of cards that comes back to the owner, so what he marks as worth remembering
sticks. Cards are added by hand first, and later sent by other features. It is its own feature
rather than part of desk: three features will send it cards, it has its own logic for when a card
returns, and a card needs only a link back to where it came from, see "Being allowed to call is
not a reason to split" in `High-level/docs/feature-contract.md`. Until it was planned, on
2026-11-13, it was described as a part of desk, in `desk/docs/desk-architecture.md`, whose Review
section now points here. Its scope and slices are in its feature issue, #229.

It is the platform's first C# feature, on .NET 10, an ASP.NET Core minimal API app.

It has nothing to do with the brain's review queue, which is where the brain's consolidation
proposals wait for the owner, see "The review queue" in `Brain/docs/trust-model.md`.

## What it serves

- `GET /review/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what review depends on.
- `GET /review/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.
- `GET /review`, `/review/cards`, `/review/decks` and `/review/study`, with any query, the page:
  `web/dist/index.html`, revalidated on every load. Without a valid token each answers `303` to
  `/auth/refresh?return_to=<the path and query as received, URL encoded>`.
- `GET /review/assets/*`, the page's built files from `web/dist/assets/`, without a token, as the
  brain serves its own: they are the same for everyone and hold no data.
- The user's decks and cards, see "What a card is" below, each route needing a valid token, without
  which it answers `401` with an empty body, and reaching only the user's own rows, through the
  door:
  - `GET /review/api/decks`, every deck, by name without letter case: `id`, `name`, `card_count`
    and `created_at`.
  - `POST /review/api/decks`, `{"name": ...}`, a new deck, `201`.
  - `PATCH /review/api/decks/{id}`, `{"name": ...}`, renames it, `200` with the deck.
  - `DELETE /review/api/decks/{id}`, `204`, or `409` while it holds cards.
  - `GET /review/api/cards`, the cards, newest first, at most 500, each as a single read answers
    it, narrowed to one deck by `?deck=<id>`, to one topic by `?topic=<slug>`, or both.
  - `POST /review/api/cards`, `{"deck_id", "front", "back", "topics": [{"slug", "name"}],
    "source": {"path", "title"} | null}`, a new card, `201`, with `Location` its address. `source`
    may be left out. It is `new` and due at its creation time.
  - `GET /review/api/cards/{id}`: `id`, `deck` (`id` and `name`), `front`, `back`, `topics` in their
    order, `source` or null, `state`, `due`, `last_review`, `version`, `created_at` and
    `updated_at`.
  - `PATCH /review/api/cards/{id}`, `{"version", "deck_id"?, "front"?, "back"?, "topics"?,
    "source"?}`, `200` with the card. It changes the fields given and leaves the rest: a field
    left out stays, and one given as null is changed, so `"source": null` takes the source away.
    Topics are replaced whole. It never touches the schedule, and moves `version` on. `version`,
    the one the change was made from, is required, and one that is not the card's current version
    is `409` and changes nothing, so two tabs cannot silently overwrite each other.
  - `DELETE /review/api/cards/{id}`, `204`, its topics and its grades with it.
  - `GET /review/api/queue`, the cards due at or before now, oldest due first, at most 200, each as
    a single read answers it plus `previews`, `{"again", "hard", "good", "easy"}`, each the time
    that grade, given now, would bring the card back, in `{"cards": [...], "total": n}`, where
    `total` is how many are due in all. Narrowed to one deck by `?deck=<id>`, to one topic by
    `?topic=<slug>`, or both, and `total` with them.
  - `POST /review/api/cards/{id}/grades`, `{"rating": 1-4, "version"}`, grades the card: Again 1,
    Hard 2, Good 3, Easy 4. `201` with the card as it now is and its new `previews`. The `version`
    is the one the grade was given from, and one that is not the card's current version is `409`
    and changes nothing, so a grade sent twice counts once. See "When a card comes back" below.

  Each answers a problem document when it does not do what was asked: `404` for an id in the path
  the user does not have, another user's included, the same answer either way; `409` for a deck
  name the user has in any letter case, a deck holding cards, or a stale `version`; and `422` for a
  rule broken, a `deck_id` the user does not have included, with `errors` naming each field as it is
  on the wire, such as `front`, `topics[1].slug` or `source.path`, and `detail` saying why. A body
  that is not JSON, or JSON of the wrong shape, such as a number where text belongs, is ASP.NET
  Core's own `400`, before any rule is read. Numbers are JSON numbers, never strings holding one.

Everything else is `404`, and any request that changes state from another origin is `403`, before
routing, see "How review authenticates a request" below. An error the service did not plan for is
an RFC 9457 problem document, `application/problem+json` with `status`, `title` and `detail`,
through ASP.NET Core's `AddProblemDetails`, which the page's client reads as the memory centre's
reads the brain's.

## What a card is

A card is something worth remembering, a question and its answer: a **front** and a **back**, both
plain text, each 1 to 4000 characters once surrounding whitespace is trimmed, stored trimmed with
the line breaks inside kept. Characters are counted as Postgres counts them, so an emoji is one.

It lives in exactly one **deck**, review's own organisation, which is how the owner chooses what to
study. A deck has a name, 1 to 100 characters, stored trimmed, unique among the user's decks in any
letter case. A deck holding cards cannot be deleted; its cards are moved or deleted first.

It is about one to five **topics**, the brain's vocabulary, so a grade can later tell the brain
what the owner knows. Review never keeps topic tags of its own. Each topic is the brain's slug, by
the brain's rule, lowercase letters, digits and hyphens, starting with a letter or digit, 1 to 80
characters, with the brain's name for it when the card was saved, 1 to 200 characters, kept as
given: a snapshot, as "A reference to another feature's record is an address plus a snapshot" in
`High-level/docs/feature-contract.md` describes. The slugs on a card are distinct, and kept in the
order given. Review does not ask the brain whether a topic exists: the page picks or creates it in
the brain before saving, and a slug the brain does not know is still a valid name for one, which
an event later makes provisional. So making a card works while the brain is down.

It may have a **source**, the record of another feature it came from, as an address and a
snapshot: a `path` on the platform, at most 500 characters, and its `title` then, 1 to 200
characters. The path is held to the core's rule for a return path, "A return path is only ever a
relative path" in `Core/docs/core-architecture.md`: it starts with one `/` and never two, which
would name another host, and holds no backslash, control character or whitespace, since a browser
reads `\` as `/` and drops tabs and newlines, which turns `/\evil` and `/<tab>/evil` into `//evil`.

And it has its **schedule**, FSRS-6's card state as `py-fsrs` 6.3.3's `Card` holds it: `state`
(`new`, `learning`, `review` or `relearning`), `step`, `stability`, `difficulty`, `due` and
`last_review`. A new card is `new` and due at its creation time; grading reads and writes the rest,
see "When a card comes back" below, and changing a card never touches it, nor does moving it to
another deck. Its `version` moves on with every change, a grade included.

The tables are `decks`, `cards` and `card_topics`, in `migrations/0002_decks_and_cards.sql`, which
checks the same rules again. Each is scoped as "The door" below describes, and every reference
between them carries `user_id`, as the brain's do: a foreign key is checked without row level
security, so one on the id alone would let a card sit in another user's deck.
`IsolationTests.ARowCannotPointAtAnotherUsersRow` checks it. Time comes from the injected
`TimeProvider`, never the clock, and ids are UUIDv7s made by review with `Guid.CreateVersion7`, from
that time.

The routes under `/review/api/` are pinned by `contracts/review/openapi.json`, see "The schema"
below.

## When a card comes back

Grading a card schedules it by FSRS-6, the Free Spaced Repetition Scheduler, Anki's default since
2023, chosen over SM-2 and Leitner boxes with the owner: it predicts forgetting better, and avoids
SM-2's ease hell, where a card failed a few times stays too frequent for good. FSRS models each
card's memory by a stability, the days until the chance of recall falls to 90%, and a difficulty
from 1 to 10. A new card goes through two learning steps, 1 and 10 minutes, before it is scheduled
in whole days by its stability; a card forgotten in review goes through one relearning step of 10
minutes. Each of the four grades moves stability and difficulty by FSRS's formulas, and the next
interval is the one at which recall is predicted to fall to 90%.

It is written here, `src/Review/Scheduling/Fsrs.cs`, because no .NET port is
trusted as a dependency, and it ports `py-fsrs` 6.3.3's `Scheduler`
(https://github.com/open-spaced-repetition/py-fsrs) expression by expression. It is pure:
`Fsrs.Grade(state, rating, now)` gives the next state and `Fsrs.Preview(state, now)` the four
dues, from `Grade` four times, and neither reads the clock. `FsrsTests.EveryReferenceVectorIsReproduced`
holds it to `py-fsrs` value for value, through `tests/Review.Tests/Scheduling/fsrs-vectors.json`,
which `tests/tools/fsrs_vectors.py` writes from `py-fsrs` itself. From the repository root:

```zsh
uv run --with fsrs==6.3.3 python review/tests/tools/fsrs_vectors.py \
  > review/tests/Review.Tests/Scheduling/fsrs-vectors.json
```

It is not run in CI, since the reference changes only when the version does, and adds nothing to
any lockfile.

**The settings** are `py-fsrs` 6.3.3's defaults, as constants in `Fsrs`: its 21 parameters, desired
retention 0.9, the learning and relearning steps above, a maximum interval of 36,500 days, and
fuzzing off. Fuzzing spreads due dates at random so cards learnt together do not stay together; it
is off so intervals are deterministic and testable, and adding it later means an injected random
source. The parameters are FSRS's defaults, fitted to many people's reviews; fitting them to the
owner's own needs hundreds of his grades first, which is why they are kept.

**Grading**, `POST /review/api/cards/{id}/grades`, reads the card, runs `Fsrs.Grade` at the
request's time from the injected `TimeProvider`, and in one transaction updates the card only
`WHERE id = @id AND version = @version`, writing its schedule, `last_review` and `version + 1`, and
logs the grade only when that update changed a row. Every change to a card moves its version, so
the state graded from is the state replaced, and a stale or repeated grade is a `409` that changes
nothing. `updated_at` stays: it says when the card itself was last edited.

**The grade log**, `grades` in `migrations/0003_grades.sql`, keeps every grade from the first: the
rating, when it was given, the days since the card's last grade with their fraction, 0 for the
first, and the card's state, stability and difficulty before and after, with its due after. It is
what later fits FSRS's parameters to the owner's memory, and a review's history cannot be
recreated, so it is history: `review_app` inserts a grade and never changes or deletes one, which
the migration enforces by taking `UPDATE` and `DELETE` back, leaving only `UPDATE` of the
`brain_*` columns, which mark whether the brain has the grade, a later slice. Deleting a card
deletes its grades, since the cascade runs as the table's owner. Like every reference here, a
grade points at its card with `user_id`.

**The queue**, `GET /review/api/queue`, is the cards due at or before now, oldest due first, with
each one's previews, so the page can show on each button when that grade would bring the card back.
A new card is due when it is made, so it is in the queue at once.

## The page

`web/`, a frontend like any other, a `web` folder under its service, so CI and the agents gate and
build it with the rest, see `design/ui/README.md`. It is the memory centre's stack at its versions,
`Brain/web/package.json`, and depends on the components as
`"@platform/ui": "file:../../design/ui"`. Its `index.html` includes the nav's lines from
"Navigation" in `High-level/docs/platform-architecture.md`, with `<platform-nav current="review">`,
and its content sits in `<main class="page">`.

The page is an `h1`, "Review", a line under it, then the package's `Tabs`, one per section, each
with its own address:

| Section | Address         |
| ------- | --------------- |
| Study   | `/review`       |
| Cards   | `/review/cards` |
| Decks   | `/review/decks` |

`/review/study` is the address of a study session, which a later slice builds, and shows the Study
tab until then, which shows the package's `EmptyState` until its slice lands. The address is the
state, through the History API and no router library (`src/address.ts`): a tab pushes its
section's address, Back and Forward come back as `popstate`, and an address that names no section
shows a not found state under the tabs. The list of sections lives in `src/sections.ts`, what each
shows in `src/app.tsx`, and the addresses the service answers the page for in
`src/Review/Pages/PageEndpoints.cs`; the sections and the addresses change together.

### Cards

`src/cards/`. A `Select` "Deck" filter, "All decks" by default, and "Add card", the tab's one
primary button, above the cards, newest first, in the package's `Table`: Front, its first line cut
at 120 characters, Deck, Topics as `Chip`s, and Next due, a relative time ("in 3 days", "now" once
it is due) with the exact time as its title. On phone the table's column priority folds Deck,
Topics and Next due into one mono line under Front, so each card is a stacked row with the same
fields. Each row has Edit and Delete, ghost buttons. With no decks the tab says "Make a deck
first", with a link to Decks; with decks and no cards, "No cards yet" with "Add card".

"Add card" opens the form in a `Card` above the list: `TextArea` "Front", `TextArea` "Back",
`Select` "Deck" with the owner's decks and "New deck…", which turns into a `TextField` for the
name and makes the deck when the card is saved, and the topic picker "Topics". Each is required and
refused empty in the form, before anything is sent, and a `422` from review shows on the field it
names. Save is the form's primary button and Close a ghost one, and Ctrl or Cmd with Enter saves.
After a save the form stays open with the deck and topics kept, Front and Back cleared and focus on
Front, so adding ten cards after a lecture is front, back, save, ten times, see "The capturing
costs almost nothing" in `desk/docs/desk-architecture.md`. It remembers the last deck and topics in
`localStorage` under `review-last-card`, a per browser convenience read and written inside `try`
and ignored when unavailable, and offers them on the next visit, dropping silently a deck or topic
that no longer exists and offering a topic that has since become an alias as the topic it resolves
to.

Edit opens the same form in the card's row, which the table draws as selected, and Save sends the
card's `version`, so a card changed elsewhere since is refused with `409` and said in a toast.
Delete asks in a `Dialog`, "Delete this card? Its review history goes with it.", then says "Card
deleted", without undo. One form is open at a time, so the view has one primary button.

### Decks

`src/decks/`. The decks by name, each with its card count as a `Badge`, "Add deck" as the tab's
primary button, and Rename, in place, and Delete, ghost buttons, Delete asking first in a `Dialog`.
A duplicate name shows review's `409` on the field. A deck holding cards cannot be deleted, so its
Delete is disabled, with "Move or delete its cards first." as its description and title.

### The data, and the brain's topics

`src/providers.tsx` wraps the page in the package's `ToastProvider`, the two clients below, and
React Query, as the memory centre's does: every failed call raises an error toast in one place,
except a change marked `meta: { fieldErrors: true }`, whose `409` or `422` its form shows on the
field, and a query marked `meta: { ownError: true }`, whose view says it failed itself. Review's own
queries live under `["review"]`, and every change refreshes every query under it, so a deck's
count and a card's deck name are never stale. The page's clock is passed in there too, so a test
sets the time.

`src/api/client.ts` is the page's one way to call a service, as the memory centre's client is, one
implementation over a generated schema instantiated twice: review's, rooted at `/review/api/`, and
the brain's, rooted at `/brain/api/`. Every call goes there with the session cookie, a `401` is
refreshed once, by posting to `/auth/refresh` and repeating the call, and a second `401` sends the
browser to `/auth/refresh?return_to=<path>`. The two instances share one refresh, so calls to
either that get a `401` together wait for the same one. Their GETs are typed from
`src/api/schema.d.ts` and `src/api/brain-schema.d.ts`, and a problem document's `errors`, the
fields a `422` names, are read for the form.

A card's topics are the brain's vocabulary, so the page picks them from the brain's own API,
directly from the browser with the session cookie: the owner holding his token, not review calling
another feature, see "Display and navigation are composed in the browser" in
`High-level/docs/feature-contract.md`. Review's backend never calls the brain for topics.
`GET /brain/api/topics` is read once per opening of a form, cached by React Query under
`["brain", "topics"]`, and `POST /brain/api/topics` with `{"name"}` creates one, canonical, since
the owner named it deliberately, and adds it to that cache. A card stores a topic's slug and its
name at the time, the slug of the topic an alias resolves to when an alias is picked.

The topic picker, `src/topics/topic-picker.tsx`, is the ARIA combobox pattern, a text field that
controls a listbox, built from the package's `TextField` and `Chip`. Typing filters the owner's
topics by name and slug, an alias shown as the topic it resolves to; the arrow keys move the active
option, Enter or a click picks it, and a name no topic has is offered as "Create topic <name>".
Each picked topic is a `Chip` with a ghost button "Remove <name>", Backspace in the empty field
removes the last, and a card takes at most five. When the brain cannot be reached the picker says
"Topics are unavailable while the brain cannot be reached", and the form's Save is disabled with
that as its description, since a card without a topic cannot be saved; the rest of the page works.
It stays in review's frontend, not `@platform/ui`, until a second feature needs it, as
`design/ui/README.md` says.

The tests render the real page at an address over `src/test/fake-platform.ts`, an in-memory stand
in for review's deck and card routes and the brain's topic routes behind the clients' fetch, which
keeps the rules the page relies on. The picker's are checked with axe, `axe-core` run against the
form in jsdom, contrast aside, which jsdom cannot compute.

## The schema

`Microsoft.AspNetCore.OpenApi` generates the OpenAPI document of the API at
`GET /review/api/openapi.json`, needing no token, only when `REVIEW_EXPOSE_OPENAPI=1`, so the
running service serves it nowhere. `ContractTests` writes it with keys sorted, a two space indent
and a trailing newline and checks that it equals `contracts/review/openapi.json` byte for byte,
see `contracts/README.md`. Every route has a summary and named response types, so a change to
either is a diff to review. After changing a route, from this directory:

```zsh
UPDATE_CONTRACTS=1 dotnet test --filter "FullyQualifiedName~ContractTests"
cd web && npm run generate:api
```

The second line writes `web/src/api/schema.d.ts` from the file with `tools/generate-schema.mjs`,
the memory centre's tool, and `web/src/api/brain-schema.d.ts` from `contracts/brain/openapi.json`
the same way, for the brain's topics, and `src/api/schema.test.ts` fails when either is stale. Run
it after the brain's contract changes too. The app is named
for its own assembly, so the document is the same under the tests' host as in the service.

## Running it

From this directory, with the .NET SDK `global.json` pins installed, and the page built first,
since review refuses to start without `web/dist/index.html`, naming it:

```zsh
(cd web && npm ci && npm run build)
REVIEW_APP_DATABASE_URL="postgres://review_app:<password>@127.0.0.1:5432/review" \
  dotnet run --project src/Review -c Release --no-launch-profile
```

The database is migrated first, as the owner, by `dotnet run --project src/Review -c Release
--no-launch-profile -- migrate`, see "Migrating" below. Locally, from the repository root,
`bash scripts/agent/services.sh run review migrate` and `bash scripts/agent/services.sh run review
start` do both, with the strings built from `infra/.env`.

`dotnet run` runs it from this directory, where `web/dist` is, which `RunWorkingDirectory` in
`src/Review/Review.csproj` sets; the Web SDK would otherwise run it from the project's folder. Its
entry in `infra/services.json` starts it the same way, with `REVIEW_LISTEN_ADDR` set to the host
and port the manifest gives. Port 8083 sits beside the brain's 8082, and the Caddyfile's upstream
for `/review` points at it.

Its tests, the steps `.github/workflows/dotnet.yml` runs, against the Postgres in `infra/`:

```zsh
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore -c Release
REVIEW_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" \
REVIEW_APP_DATABASE_URL="postgres://review_app:$(grep '^REVIEW_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/review" \
  dotnet test --no-build --no-restore -c Release
```

The tests that need a database each get one of their own, from
`tests/Review.Tests/Harness/TestDatabase.cs`: once per run it creates a template database,
migrated as `review_owner`, and each test a copy of it, through the Postgres superuser's string,
`REVIEW_TEST_ADMIN_DATABASE_URL`, which only the test step holds, the pattern of the brain's
`tests/harness.py`. Tests that act as review does connect as `review_app`, through
`REVIEW_APP_DATABASE_URL` pointed at their copy. Both are required, and unset is a failure, never a
skip. Tests that need no database take none, and run without either.

and the page's, from `web/`, the steps `.github/workflows/web.yml` runs for every frontend:

```zsh
npm ci
npx biome ci .
npx tsc --noEmit
npx vitest run
npm run build
```

`packages.lock.json` is committed beside each project; after changing a package, run
`dotnet restore` without `--locked-mode` once to update it, and commit the change. The default
`.editorconfig` wants no final newline in a C# file, and `dotnet format --no-restore` fixes one.

## Settings

Review reads its settings from the process environment, once, at startup, into one immutable
`ReviewSettings`, in `src/Review/Settings/ReviewSettings.cs`. It loads no `.env` file. Unset means
the default, and a value that is set must parse, or review refuses to start, naming the setting,
rather than quietly using the default. The rules are canary's, written again rather than shared,
since no service imports from another's directory.

- `REVIEW_LISTEN_ADDR`, default `127.0.0.1:8083`: where Kestrel listens, a host and a port, an
  IPv6 host in brackets.
- `REVIEW_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `REVIEW_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.
- `REVIEW_EXPOSE_OPENAPI`, default `0`: `1` maps the schema's route, for the contract test and
  regenerating the file, see "The schema". Nothing else is accepted.
- `REVIEW_APP_DATABASE_URL`, required to serve, with no default: review's database as
  `review_app`, `postgres://review_app:<password>@127.0.0.1:5432/review`, the password percent
  encoded. It is parsed once, by `src/Review/Settings/DatabaseUrl.cs`, into Npgsql's settings: a
  `postgres` or `postgresql` scheme, a user and a password, a host, a port, 5432 when left out, and
  the database, with no query. It holds a password, so it is never shown, in an error or in the
  settings' `ToString`, and is set on the command that starts review rather than written to a file.
- `REVIEW_OWNER_DATABASE_URL`, the same form as `review_owner`, read by `migrate` alone, see
  "Migrating". Serving never reads it, and `services.sh` unsets it for `start`.

The defaults are right on the Mac mini and in CI, so neither sets them, except the listen address,
which the manifest's `start` sets, and the two database strings, which `services.sh` builds from
`infra/.env`. Later slices add `REVIEW_DAY_ZONE` and `REVIEW_BRAIN_URL`.

## The database

Review has a database of its own, `review`, beside the core's and the brain's, in the one Postgres
in `infra/`, created with its two roles by `infra/postgres/initdb/review.sh`, see "The databases"
in `infra/README.md`. Why a database per service, and why the roles are split, is in "Databases,
migrations, and running it" and "Multi user, and how isolation is enforced" in
`High-level/docs/platform-architecture.md`.

- **`review_owner`** owns the tables and runs the migrations. Nothing serving connects as it.
- **`review_app`** is what review serves as. It owns nothing, so row level security applies to it,
  and `migrations/0001_default_privileges.sql` grants it `SELECT`, `INSERT`, `UPDATE` and `DELETE`
  on every table the owner creates and `USAGE` on every sequence, never `TRUNCATE`, and takes
  `CREATE` on `public` from everyone but its owner. A table that needs less revokes it in its own
  migration. Unlike the brain, which deletes nothing, review deletes cards.

### Migrating

Migrations are plain SQL files in `migrations/`, named `NNNN_snake_description.sql`, forward only:
an applied file is never edited, renamed or removed, and a change is a new file. They are embedded
in the assembly, so the command reads them wherever it runs. They run only as a command, never at
startup, since the app role is not allowed to:

```zsh
REVIEW_OWNER_DATABASE_URL="postgres://review_owner:<password>@127.0.0.1:5432/review" \
  dotnet run --project src/Review -c Release --no-launch-profile -- migrate
```

The command, `src/Review/Database/Migrations/`, is the brain's `brain migrate` written again in C#,
with the same rules. It reads `REVIEW_OWNER_DATABASE_URL` and nothing else, takes a session
advisory lock, whose key is the first 8 bytes of SHA-256 of `review.migrate` as a big endian
signed 64 bit integer, so two runs cannot overlap, and records each file in `schema_migrations
(version, name, applied_at)`. Each new file runs in its own transaction with its ledger row, so a
failing file leaves nothing behind and stops the run before any later file. It prints `applied
<version> <name>` per file, or `nothing to do, every migration is applied`. It refuses, applying
nothing, when the files and the ledger disagree: a file name it cannot order, two files with one
version, an applied file renamed or gone, or a new file numbered below one already applied. With
no argument review serves, and any argument but `migrate` is refused with the usage.

### The door

Every query goes through `ReviewDb.InUserTransaction(userId, work, cancellationToken)`, in
`src/Review/Database/ReviewDb.cs`, which takes a connection from the pool, opens a transaction,
sets `review.user_id` for that transaction alone with `set_config('review.user_id', @id, true)`,
the `SET LOCAL` the isolation section requires, runs the work with the transaction, and commits,
or rolls back if the work throws. It refuses the empty user id, which names nobody. The work writes
its SQL out, mapped to records with Dapper, so the query a policy guards is the one in the code.

`ReviewDb` holds one `NpgsqlDataSource` as `review_app`, opened at startup with one connection,
so a database it cannot reach, or a wrong password, stops review there, naming
`REVIEW_APP_DATABASE_URL` and never its password. It opens every connection review makes, the
owner's one for `migrate` included, and `IsolationTests.OnlyTheDoorOpensConnections` checks by
reflection that no other type in the assembly holds a connection or a data source in a member.

Every scoped table has `user_id uuid NOT NULL`, row level security enabled and forced, and one
policy, `USING (user_id = nullif(current_setting('review.user_id', true), '')::uuid)` with the same
`WITH CHECK`, the expression `ReviewDb.Guard` holds. With no user set it is null, so a query
outside the door returns nothing rather than everything, and a row written for another user is
refused. `tests/Review.Tests/IsolationTests.cs` proves it on a probe table, and fails for any table a
later migration adds to `public` without exactly that one policy, `schema_migrations` aside. There
is no path around the policies yet: purging and exporting, if review needs them, are planned later.

## How review authenticates a request

The rules every feature follows are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`. Review holds none of the code for them: `PlatformAuth`, in
`libs/dotnet/PlatformAuth/`, does every check of the token and the origin, and its README says what
it holds and how its tests cover the ways verification breaks. Review references it as a project
and does three things with it, in `src/Review/ReviewApp.cs`:

- `AddPlatformAuth` with `REVIEW_CORE_URL`, which registers the key cache, the handler and the two
  policies. The pages take `PlatformAuthPolicies.Page` and `whoami` takes
  `PlatformAuthPolicies.Api`; health and the built files take neither.
- `UsePlatformOriginCheck` with `REVIEW_PUBLIC_ORIGIN`, before routing, so every route, one added
  later included, is covered: the decks' and cards' `POST`, `PATCH` and `DELETE` among them.
- `ReviewApp.Build(settings, webRoot, configure)` builds the app; `configure` runs before it is
  built, which is where `Program` sets the listen address and where the tests put the app on a
  test server, with a key cache over the committed key set answered from memory and a clock fixed
  at the committed token's `iat`, both of which `AddPlatformAuth` keeps.

The tests send the ways verification is known to break through review's own routes, as "A
feature's authentication tests are part of the feature template" in
`High-level/docs/how-we-build.md` asks: the committed token is accepted from the cookie and the
header, a missing, expired, wrongly signed or unknown key id token gets `401` from the API, and a
page without a token answers `303` to the refresh endpoint with the way back. That every kind of
invalid token is the same to a page is `PlatformAuth`'s own test.

## Libraries review is built on

**The .NET 10 SDK, pinned in `global.json`**, with `Directory.Build.props`,
`Directory.Packages.props` and `.editorconfig` holding the same values as `PlatformAuth`'s folder,
copies rather than shared files, since each C# folder builds from its own directory alone. Why .NET
10, the analyzers as the linter and `dotnet format` as the formatter are in "Libraries it is built
on" in `libs/dotnet/PlatformAuth/README.md`.

**One web project, not layered projects.** `src/Review/` is the whole service, organised in
feature folders: `Settings`, `Database` (the door and the migrations), `Health` (health and whoami),
`Pages`, `Decks`, `Cards`, `Scheduling` (FSRS, grading and the queue), and `Api`, what the routes over the user's data share (who is asking, the
problem documents, and the rule collector a 422 is made from), more with later slices. The service is small, and folders inside one project keep it readable without Clean
Architecture's ceremony.

**ASP.NET Core minimal APIs**, from the shared framework, for routing, static files and problem
details, with no MVC. JSON is snake case on the wire, as the brain's and canary's is.

**Microsoft.AspNetCore.OpenApi 10.0.15 for the schema**, the generator that ships with ASP.NET
Core, so the document follows the routes and their types without annotations of a second library.
Swashbuckle was the alternative, which ASP.NET Core no longer includes by default.

**Npgsql 10.0.3**, the PostgreSQL driver for .NET, **with Dapper 2.1.89** for mapping rows to
records, with the SQL written out where it runs, the same shape as sqlx in the core and psycopg in
the brain, so the query a policy guards is visible. EF Core was the alternative and was turned
down: migrations here are plain SQL, so its migrations would go unused, its row level security
setting would sit in an interceptor rather than in one visible door, and its generated SQL makes
"which query does the policy guard" harder to read.

**A migrator of its own, not DbUp.** DbUp's journal does not refuse an applied file that was
renamed or removed, which forward only depends on, and the brain's rules are short enough to write
again.

**xUnit v3 for the tests**, with `Microsoft.AspNetCore.Mvc.Testing` for `TestServer` and
`Microsoft.Extensions.TimeProvider.Testing` for the clock, the five test packages `PlatformAuth`
uses, at its versions.

**The page** is the memory centre's stack, React, Vite, Vitest, Biome, TypeScript and
`@tanstack/react-query` 5.107.1, at its versions; see "Libraries the brain is built on" in
`Brain/README.md` for why each was chosen. React Query came with the Cards and Decks tabs, the first
sections to load data.

**axe-core 4.14.0**, a dev dependency at the version `design/ui` pins for the accessibility checks
in `e2e/`, checks the topic picker's ARIA in its Vitest tests. No new library came with it: both
were already pinned in the platform.
