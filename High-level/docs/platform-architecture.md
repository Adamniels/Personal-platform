# Platform architecture

Status: exploratory. The walking skeleton is being built.

Last updated: 2026 11 15

The top of the documentation. What the platform is, what services exist, and the thinking that
shapes more than one of them. Anything you only need when you sit down to write a particular
service lives in that service's folder instead, `Brain/docs/` or `Core/docs/`.

How to read these documents, and how firmly, is in `docs/README.md`.

**Read next**

- `High-level/docs/feature-contract.md` — what a feature is, and what it may depend on
- `High-level/docs/how-we-build.md` — how the work is cut up and ordered
- `Core/docs/` — the core in depth
- `Brain/docs/` — the brain in depth, including what it is trying to know and how evidence
  gets in

**A naming convention.** The platform is multi user. Where something says "the owner" it means
whoever owns that data, and it holds for every account. Where an example says "Adam" it is an
illustration from the first user's case, not a special role.

Related Notion pages: "Personal Platform" in Desk, the owner's planning notes, which hold the
candidate features. Older: "Northstar OS" (project entry), "Northstar OS, my personal platform",
"Wiki project". v1 material lives under "My Platform (1)" and "Implementing memory" and is
reference only, see "Carried from v1" at the bottom of `Brain/docs/brain-architecture.md`.

---

## What this is

The second version of a personal platform. A stable core plus features built one at a time,
independently. A shared brain, meaning memory, is what makes it a platform rather than a
collection of apps, and it is the part most worth getting right.

The ambition is large, and it is worth saying because a few things only make sense at that
size. This is meant to become the owner's main tool in daily life, with features added over
years rather than a small fixed set that gets finished. Three things follow from that. Knowledge
about the owner flowing through the brain, rather than features asking each other, is what keeps
growth linear instead of quadratic, which matters more the more features there are. The front page is a real surface rather than a list of links. And the
cost
of running several languages is paid once per feature rather than once, by one person, so it is
a running cost rather than a one time one.

## The framing

Two questions that kept getting bundled together, and are worth holding apart:

1. **Where does a feature's UI and data live?** Isolated. Own frontend, own backend, own
   database.
2. **What is a feature allowed to depend on?** A small fixed set of platform contracts, and
   not much else.

Isolation of implementation, coupling through contracts. Most of what follows comes out of
keeping those two separate.

---

## The brain is its own service

It owns two things, memory and cross feature brokering, and it has its own database.

The reason it is separate rather than part of a general backend: if memory lives inside a
general backend, that backend slowly absorbs every feature's data, because it is always the
path of least resistance. Making the core a client of the brain rather than its owner is what
keeps that honest.

### The brain as a dependency

Features use the brain as much or as little as they want. Most will lean on it, some barely,
and one that never touches it is fine. This says nothing about which.

For the ones that do use it, nothing is built to soften the dependency. No graceful degradation
paths, no fallback adapters, no speculative portability seams. A feature that uses the brain
assumes the brain is there.

Extraction is not something to design for. If a feature ever had to become a standalone
product, you would rebuild whatever slice of brain behaviour that feature actually used, and
how much work that is depends entirely on how deep it went. That cost is accepted rather than
engineered around.

### Profile lives in the brain

Identity, goals, values, interests, preferences, and how the owner wants to be worked with.
All of it explicitly entered, and any context read has to merge it with inferred memory, so
splitting it across services would mean a network join on every read.

The line to hold: the core owns who you are as an account, the brain owns who you are as a
person.

Skill level is deliberately not in here. Profile holds the things the owner is definitionally
right about, so nothing in it moves except by his own hand. A self assessment is something he
can be miscalibrated about, so level sits in the knowledge model where evidence is allowed to
move it. `Brain/docs/trust-model.md` has the full picture of which store holds what and who can
change it.

### No model calls on the read path

Reads are SQL plus vector, deterministic, tens of milliseconds. All the expensive reasoning
happens in consolidation, offline. That is what makes coupling every feature to one shared
brain safe on latency grounds.

A feature fetches a context packet once per run, not per page render. Writes are events, fire
and forget, never blocking a user action.

Caching the profile snapshot is fine but has to invalidate on write, which is trivial at this
scale. A directly written fact has to affect the very next request, so a stale cache quietly
breaks that.

---

## Languages

- **Rust** for the core and for infrastructure services.
- **Python** for the brain.
- **TypeScript** for feature frontends, probably React.
- **Each feature picks its own** when that feature gets planned.

The number of languages is deliberately not capped.

**Why the brain is Python.** Its hardest work is consolidation, embeddings, clustering and
model orchestration. v1's real failure was splitting memory across two languages, and one
service in the language where its hardest work lives removes that split by construction.

**Why the core is Rust.** Less about the language than the direction it pushes. C# invites a
richer domain model and heavier features; Go invites small boring services; Rust is Go's
direction, more strongly. Building a rich domain model in Rust is possible but high friction,
and the core is supposed to hold nothing interesting anyway, so the friction backs the goal
instead of fighting it.

One thing that is easy to get backwards: a thin core does not push complexity into the brain.
The brain is not allowed to decide anything in a feature's domain, so complexity displaced from
the core lands in *features*.

Two things came with the Rust choice. Putting `projects` inside the core codebase stopped being
tempting, since its only argument was that both would have been C#. And authentication became
work rather than something adopted, since Rust has excellent primitives and no assembled auth
system. That is wanted as practice, and `Core/docs/core-architecture.md` covers how it gets
built and where the line sits.

**The core's frontend is not TypeScript.** It renders HTML from templates in Rust, because an
application cannot serve a page composed on the server in one response, and that is what the front
page's design rests on. Feature frontends are unaffected.
`Core/docs/core-architecture.md` has the reasoning and says where it would stop being the right
tool.

**Why feature languages are not picked here.** An earlier list assigned wiki, learning and news
to Python and projects to C# before any of them had been designed, which is a guess wearing the
clothes of a decision. Nothing constrains the choice, so it belongs to the planning phase for
that feature.

**Mobile** is deferred entirely. Swift or Expo, much later, and partly determined by whether the
web frontends end up sharing enough to be worth reusing.

**What running several languages costs**, accepted knowingly: separate builds, separate tooling,
no shared types, and contract drift caught at runtime. The mitigation is schema first contracts,
OpenAPI or protobuf, with generated clients on both sides, and it is worth committing to early
rather than retrofitting.

---

## Multi user, and how isolation is enforced

The platform is built for one person today and will only ever have a handful of accounts. It is
still multi user from the first schema, because retrofitting a user scope onto an append only
event table and a claim store is a migration nobody enjoys and everybody postpones. Adding the
column before there is any data costs nothing. This is the one case where the cheap option now
and the correct option later are the same option.

**Everything in the brain is scoped to a user.** Events, claims, records, profile, procedural
rules, the knowledge model and topic vocabulary. There is no unscoped memory of any kind, and
no unscoped call in either direction of the feature contract. Consolidation runs over one
user's corpus, never the whole table.

Those two are written as rules rather than intentions, and here is what goes wrong without
them: a missing scope filter is not a wrong answer, it is one person's data appearing in
another person's account, and during consolidation it is the difference between a clustering
bug and one user's evidence being written into someone else's memory.

**Isolation is enforced by the datastore, not by query discipline.** Wherever a datastore can
refuse to return another user's rows, it does, and application code is never the only thing
standing between two accounts. For Postgres that means row level security. A feature on a store
that cannot do this says so and says what it does instead.

The reason is that the two approaches fail in opposite directions. Query discipline fails open:
a forgotten `WHERE` clause returns another person's rows, raises nothing, and looks like a
working feature. Row level security fails closed: the same forgotten clause returns zero rows.
The class of bug does not get rarer, it stops being able to leak.

Four things have to hold for that to be real, and each one is a way to have it switched off
while looking configured:

- The application connects as a role that does not own its tables, since owners bypass policies
  by default. Migrations run as the owner.
- `FORCE ROW LEVEL SECURITY` on every scoped table, so ownership is not a bypass either.
- The user identity reaches the database as a transaction scoped setting, `SET LOCAL`, set
  inside the transaction. Plain `SET` on a pooled connection survives into whatever request
  borrows that connection next, which is a cross user leak caused by the mechanism adopted to
  prevent them. The footgun does not disappear, it moves from every query site to one place in
  connection handling, which is the point.
- Maintenance access is a named, deliberate bypass path rather than an ad hoc superuser
  connection.

What it costs: a query returning nothing has a reason that is not visible in the SQL, so
debugging gains a first question. Every connection checkout needs the setting, including
background jobs and anything run by hand. Policies add a predicate to every plan, though
`user_id` belongs in those indexes regardless.

**Isolation does not foreclose sharing later**, which is why the isolated version is cheap
rather than limiting. Sharing, if it ever comes, belongs inside a feature. From the wiki's point
of view a collaborator is simply another account that logged in, so the wiki can share a page
without the brain sharing anything. The boundary sits under the features rather than across
them.

**The user id is minted by the core and by nothing else.** Every other service stores what it was
handed and never interprets it, so the only thing that has to be right is the value itself. It is
a UUID rather than a counter, so two databases can never disagree about who account five is, and
if an identity provider is ever dropped in later its subject identifier gets mapped to an account
rather than becoming the user id. `Core/docs/core-architecture.md` has the shape and why changing
it later is close to impossible.

**The core is outside this rule**, because the queries that establish identity run before there is
a user to scope to. The rule holds for the brain and for any feature holding per user data.
`Core/docs/core-architecture.md` has the argument.

---

## The core's frontend

The core has a frontend, and the front page is its main screen. It is what you see after logging
in, and it is where you go to reach anything else.

It is not a wrapper. A feature's frontend is its own page on its own process, and the proxy
routes you to it, so clicking through to the wiki means leaving the front page and arriving at
the wiki's. Nothing is contained inside anything.

**The front page is composed on the server.** When you build a feature you decide whether
anything from it belongs on the front page. If it does, the feature exposes an endpoint with
display data, and the core calls it, assembles whatever came back, and serves the page in one
response.

Two things follow, and both are the point rather than a compromise. The feature sends data, not
rendered output, so the core decides how everything looks and the page stays coherent instead of
becoming a patchwork of five ideas about layout. And a plain data endpoint can be read by
anything later, a mobile client or something not thought of yet, which a rendered card cannot.

None of this is a contract. A feature is not required to expose anything, and what it sends is
decided when that feature is built. `Core/docs/core-architecture.md` covers the mechanics.

**Some details worth settling once.** The core calls features on your behalf by forwarding your
token, rather than holding a service credential of its own. And the page renders with whatever
came back, so a slow or missing feature costs you its piece and not the whole page.

**Two things now reach across features, for different reasons.** The core reaches across them to
display. The brain reaches across them to know. Keeping those apart is what stops the core
drifting into interpretation and the brain into rendering.

### Navigation

Navigation is shared, so it can improve everywhere at once. Its look lives in the design folder and
its contents in the core.

The proxy serves the script with the rest of `design/`, and the core serves the endpoint it reads:

```
GET /design/nav.js  ->  defines <platform-nav>, which fetches /api/nav and renders
GET /api/nav        ->  { "features": [ {"name": "Wiki", "path": "/wiki", "icon": "book-open"} ], "user": {"email": "..."} }
```

`/api/nav` lists the features in registry order, each by its display name, its prefix and, when
its registry entry names one, its `icon`, and the logged in account's email; the account id is not in it. It needs a valid access token and answers
anything else with 401 and an empty body, never a redirect, since a script cannot follow one.
`/design/nav.js`, with `theme.js` and `page.css` beside it, is a plain file the proxy serves like
the rest of the design folder, revalidated on every load, see "The design folder" in
`infra/README.md`. The core serves no shared asset.

A feature includes these lines and nothing else, in whatever it is written in:

```html
<link rel="stylesheet" href="/design/tokens.css">
<link rel="stylesheet" href="/design/fonts.css">
<link rel="stylesheet" href="/design/page.css">
<script src="/design/theme.js"></script>
<script src="/design/nav.js" defer></script>
...
<platform-nav current="wiki"></platform-nav>
```

The bar's styles live in its shadow root and use the tokens, which the page's root defines and the
shadow root inherits, so a page that includes the bar links `tokens.css` and `fonts.css`.
`page.css` holds the page shell of section 2 of `design/DESIGN.md` around the bar, for a page
that includes `<platform-nav>` as a direct child of `body`: on desktop the room for the rail on the
left and the top bar's height, with the shell centred on a wide window, and on phone the room for
the tab bar at the bottom. The room is held before the script runs, so nothing moves when the bar
draws, and no page changes its markup for it. It also holds the page container
from section 2 of `design/DESIGN.md`, with its headings and the mono font for ids, which a page
opts into with `<main class="page">`, so a page written without a stylesheet of its own still
has the platform's layout. `theme.js` runs in the head,
before the page paints, and applies the theme the person chose, if any.

What the bar holds: on desktop a rail on the left with the mark, linking to `/`, then Home,
Memory, and one item per feature in the order `/api/nav` gives, each an icon named by its link,
and the theme toggle at the bottom. Home has Lucide's `house` icon, Memory `database`, and a
feature the icon its registry entry names. The icons live in `nav.js`, in `ICONS`, as path data
copied from `lucide-react`, and a feature's planning adds its icon there in the same slice as its
registry entry; a feature whose icon `ICONS` does not hold, or that names none, gets the fallback
`box`. Memory, to `/brain`, opens the brain's memory centre. The brain is not
a feature and has no registry entry, so the link is fixed in `nav.js`, as Home and the link to
change a password are, rather than coming from `/api/nav`. The element itself is the top bar above
the page: the crumb on the left, and on the right the account pill, the first letter of the email
and the email, opening a menu with "Change password", to the core's `/settings/password`, and "Log
out", to its logout page `/auth/logout`: links rather than forms, so the bar still only renders and
links, and both work from every feature's page without any of them changing. On phone the rail
goes: the top bar gains the mark, the pill shows the letter alone, the theme toggle moves into its
menu, and the links move to a tab bar at the bottom, where past five the first four show and More
holds the rest. A link whose entry carries `attention: true` gets the dot that marks something
needing the person; the core sends no such field yet. How each of these looks is section 3 of
`design/DESIGN.md`.

`current` names what the page is: `home` on the core's front page, `brain` in the memory centre,
otherwise the feature's prefix without the slash. The matching link is marked `aria-current="page"`, and changing the attribute
re-renders the bar. The optional `crumb` attribute names the page in the top bar, such as
`crumb="Memory / Rules"`, and is observed like `current`; without it the crumb is the name of the
link `current` names, and with neither it is empty. It writes every name, the crumb and the email
as text, and links only to a path that
starts with `/` and not `//`. When `/api/nav` fails, a 401 or a network error, it shows the mark,
Home and Memory alone, the links it holds itself: it never redirects and never refreshes the
session. The core's own pages use it
too, see "The core's frontend" in `Core/docs/core-architecture.md`, and leave it off the login and
registration pages, where nobody is logged in.

The theme is the one piece of state the bar keeps, and it is the person's, not the platform's: the
toggle writes `light` or `dark` to `localStorage["platform-theme"]` and sets `data-theme` on the
root, which `tokens.css` reads. With nothing stored, the system setting applies.

**Why this rather than a shared component package.** A package would force every feature frontend
to be React, which is a freedom deliberately left open, and changing the nav would mean bumping a
version and redeploying every feature, so the nav would only ever be as current as the least
recently touched one. A script served from one place is framework agnostic and updates everywhere
on the next page load. A new feature appears in every other feature's nav without any of them
being touched, because the list comes from the core. The script lives in `design/` rather than in
the core because it is the platform's look, which belongs with the tokens it is drawn from, while
what it lists is the core's.

**Start it as nothing.** On day one `<platform-nav>` could render a single home link, which is the
same work as hardcoding one, except the mechanism is in place. Listing features, showing where you
are, and anything later all land by editing one script, or the core's endpoint, once.

**The rule that keeps it cheap: the nav renders and links, and does nothing else.** No shared
state beyond the theme a person picked, no auth helpers, no component library riding along. That is what makes pulling a feature
out later mean deleting two lines rather than unpicking a design system, and it is the failure
mode a shared package tends toward on its own.

**The tradeoff.** The nav's contents are a runtime dependency on the core, so if the core is down
it shows the mark, Home and Memory alone. Little is lost: you could not have logged in either, a session already issued
keeps working since verification is local, and the feature itself is unaffected. `page.css`
reserves its room so the page does not jump while it loads.

**How rich the front page should be is genuinely open.** A short list of links, a page with real
content on it, or one unified product where every feature shares a design system. That is
answered by using the thing rather than by reasoning about it. Two things worth carrying into it:
the ambition of a main daily tool makes the list of links unlikely, and the unified product is
the largest permanent cost in the whole plan, larger than running several languages, because
every feature ever built has to be dragged into the same design system forever.

---

## Notifications and reminders

Delivering something to the user at a time or under a condition is its own small service. Not
the core and not the brain.

Not the core, because notification delivery changes whenever channels, schedules or templates
change, which is often, and the core must stay boring. Putting it there is the exact drift the
brain exists to avoid, a general service absorbing things because it is the path of least
resistance.

Not the brain, because it holds no memory, writes nothing to the brain and reads nothing from
it. `High-level/docs/feature-contract.md` covers why a reminder is never treated as evidence
about the person.

Scope: accept a delivery request from a feature or from the user, hold it until its time or
condition, deliver it, record that it was delivered. Nothing else.

Its language gets picked when it is designed. It is scheduling and delivery, nothing model
shaped, and it holds no memory, so Rust would fit and would extend the same practice motive
behind the core.

---

## Databases, migrations, and running it

**One Postgres instance, a database per service.** The core, the brain, and each feature that
needs one, each with its own role. Separate databases rather than schemas inside one, because a
role scopes cleanly to a database and a join across two services becomes impossible rather than
merely discouraged.

**Each service owns its own migrations.** No central runner and no ordering between services,
which is the same isolation the rest of the design has.

**Plain SQL files, numbered, forward only.** Not a migration DSL: roles, grants and row level
security policies cannot be expressed in most of them, and the brain needs all three. Forward only
because down migrations are rarely run in practice, cannot reverse the interesting cases anyway,
and the recovery path at this scale is a restore or another migration going forwards.

**Migrations are a command, never something that happens at startup.** That is a consequence
rather than a preference: the application connects as a role that does not own its tables, which
the isolation section above already requires, so it is not permitted to migrate. Two roles, two
connection strings, and only one of them is ever used from a terminal.

**Development runs on one laptop, with the services native.** Postgres and the proxy in a compose
file, since they are infrastructure that never gets rebuilt, and the services themselves run
natively, where the edit and rebuild loop actually is. **The hosted copy runs in containers**, from
`infra/hosted/`: every service built into an image and started as one compose project of its own,
the same Postgres with a database per service, migrations as a command, and configuration as the
same environment variables. See `infra/hosted/README.md`.

**It runs under a real hostname with real HTTPS from the first day**, with the proxy issuing a
certificate from its own local authority. Without that, `Secure` cookies do not behave normally
and the single origin is only ever tested against an address and a port rather than a domain, so
the cookie and origin model in `Core/docs/core-architecture.md` stays unproven until the day it is
deployed. The other half of not being shaped like a laptop is that configuration arrives as
environment variables rather than constants, so the same binary runs elsewhere unchanged.

**The hosted copy is backed up every night**, every database in it, encrypted to a key that is
never on the machine and uploaded to a bucket that cannot delete what it holds for 30 days, with a
restore that a round trip test proves; see "Backups" in `infra/hosted/README.md`. The brain is the
part of this system that cannot be rebuilt, and the hosted copy shares its machine with agents that
run unattended, so the backups are what survives a dead disk or a bad command. The brain's export
and import remain, as a way to move one account's memory to a file and into another brain, see
"Export and import" in `Brain/README.md`; they are not the backup. Nothing alerts on a missed
backup until the notifications service exists: until then the job's status file records each
attempt.

**The hosted copy is released by hand.** A release is the owner's command,
`scripts/deploy/release.sh`, run over SSH from the hosted copy's own clone. It builds a commit on
`main`, never a branch, backs up before it migrates, and never happens on a merge. The runner
merges unattended, so a merge is not a decision anyone made to put that code in front of real
data; keeping the release a separate step makes the running copy always one the owner chose, and
lets a bad merge be fixed on `main` before it reaches the data. The script sits under `scripts/`,
which the merge gate never merges, so what releases changes only by the owner's hand too. How it
fails safe and when it rolls back is "Releasing" in `infra/hosted/README.md`.

---

## One repository

The core, every feature, the brain, the infrastructure configuration and these docs live in one
private repository, each service in a directory of its own.

**A repository boundary belongs where changes do not cross it**, and here almost every change
crosses services. The Caddyfile and the registry change in the same commit, and both name
features. Putting a feature on the front page means editing the core in step with that feature's
display endpoint, with no shared shape checking that they agree. The vertical slices in
`High-level/docs/how-we-build.md` routinely touch the core and a feature in one piece of work, and
a slice sometimes shows that a document is wrong. In one repository each of those is one branch,
one pull request and one link to its issue. Split, each is several pull requests that have to
merge in the right order with nothing tying them together.

v1 is the evidence. It was split by layer, backend, frontend, workers and docs, and on almost
every day one of them changed, the others changed too. That is a boundary cutting through the
unit of change, and splitting v2 by service would recreate it along a different line.

**Repositories are not what isolates services.** Processes, separate databases and roles, and
signed tokens do that. A feature in its own repository could still call another one; a feature
in this one still cannot read another's database.

**The rule: no service imports from another service's directory, and each one builds and runs
from its own directory alone.** This is written as a rule rather than a preference, and here is
what goes wrong without it. One repository makes shared code the path of least resistance: a
helper imported across directories, a common library every feature pulls in, a root level
configuration everything quietly depends on. That is the same drift the nav rejects a shared
package over, and it is what turns splitting a service out later from a mechanical step into
untangling. The root of the repository holds only what is genuinely platform wide: the proxy
configuration and the registry, the compose file, continuous integration, contract fixtures, and
the instructions and configuration for coding agents, since those govern work on every service.

**Two things are shared on purpose, and each is written down as an exception.** Contract fixtures,
below, and shared libraries under `libs/`: code that must be identical in every service using it,
owned by no service. Token verification is the first, `libs/python/platform-auth/` and its C#
twin `libs/dotnet/PlatformAuth/`, one per language, because it is the platform's security code and a flaw fixed in one copy and missed in another is a hole nobody
sees. The database code of the C# services is the second, `libs/dotnet/PlatformData/`: the
migrator, the door that sets the user for each transaction, and the test harness, since the door is
what enforces isolation between users, and two copies of it would drift. There is no Python twin,
since the brain is the only Python service with a database. A library holds one narrow thing and never grows into a place for anything else, which is
what keeps the drift above from coming back through it. A design system for the feature frontends
is the third. Its style lives in `design/`, as values and written rules, and those files are shared
at runtime rather than imported: the proxy serves its values, fonts and sprites at `/design/`, so a
page from any service links them by URL and no service reads another's directory. "The design
folder" in `infra/README.md` lists what is served. Its components are imported, like a library:
`@platform/ui` in `design/ui/`, React that a frontend installs by path and compiles from source,
holding only components more than one feature needs, with a gallery at `/design/gallery/` showing
each one. `design/ui/README.md` says how a frontend uses it and what may go in it.

**Contract fixtures are read by more than one service from the same place.** A contract between
languages is tested by both sides against the same bytes, and those bytes belong to neither side's
directory. The first is the token: the core's tests assert that it still produces the committed
token exactly, and each feature's tests verify that token against the committed public keys. It
holds public material only. It lives in `contracts/token/` as three files: `token.jwt`, the token
minted from fixed inputs and the RFC 8037 test seed; `jwks.json`, the key set as the core serves
it for that key; and `claims.json`, the claims and key id a verifier should expect. Ed25519
signatures are deterministic, so a change to the bytes fails the core's test, and the files are
regenerated on purpose, with `UPDATE_CONTRACTS=1 cargo test --test token_contract` from `Core/`,
and the diff reviewed; `contracts/README.md` says more. Continuous integration's change detection,
`scripts/ci/changed-areas.sh`, maps `contracts/` to every area that tests against it: the core,
canary, the brain and the acceptance suite.

**What it costs, and how it is paid.** Every push would build and test every service, so
continuous integration runs only what a change touches. `.github/workflows/ci.yml` is the single
entry point: it works out from the diff which areas changed, with `scripts/ci/changed-areas.sh`,
runs each changed area's workflow, and ends in `ci-ok`, a job that always runs and passes only when
every job it judged passed or was skipped. Among them is `hosted`, which builds the hosted images
and checks a throwaway copy of them, run only when a change reaches what the images are built from,
see "In CI" in `infra/hosted/README.md`. That one check is what branch protection requires, since
a required check that only runs on some paths would block every pull request outside them. The
number of languages is not itself a cost, since each directory keeps its own toolchain and lockfile
and nothing tries to build them as one graph. Continuous integration runs cargo with `--locked`,
uv with `UV_LOCKED=1` and npm only as `npm ci`, so the lockfile is what gets tested, and a manifest changed without its
lockfile fails instead of resolving again, which `scripts/ci/workflows-locked.test.sh` holds the
workflows to.

**What would split a service out**, none of which is size: a feature that should be public while
the rest stays private, a feature becoming a product with its own releases or contributors, or someone who should work on
one service without seeing the rest. Each is a reason to move one directory out with its history,
not to abandon the arrangement, and the rule above is what keeps that cheap.

---

## The first milestone

Two tracks. They do not block each other, and saying so stops either from waiting on the other.

**Core track.** Accounts, authentication, session and token issuance, invite coded
registration, routing, the feature registry, and the core's own frontend with the front page on
it. Finishable, well understood, where the security work lives, and the track that produces
something you can actually look at.

**The core track also includes one small feature, written in Python.** Routing, the registry and
front page composition have nothing to act on while there are zero features, so all three would be
written against an imagined one. `canary` is a couple of hundred lines that exists in order to be
a feature: one page that includes the nav and verifies the token itself, a display endpoint, and a
switch that makes it hang or fail, so the front page can be watched rendering without it. It is not thrown
away afterwards. It stays as the thing to point at when the platform needs checking, and as the
template for the first real feature.

Python rather than Rust, deliberately. The least tested claim in the architecture is that a feature
can be written in any language and the coupling is the nav's few lines and verifying a token. A stub in
Rust would share libraries with the core and prove almost nothing, and if verifying the token turns
out to be awkward elsewhere, that is better found on two hundred lines than halfway through
designing a domain.

**Brain track.** Everything that waits on nothing, by the three tests in "What is built now" in
`High-level/docs/how-we-build.md`: the service with its own database and row level security, the
memory the owner enters by hand in all its kinds, events in their final shape, retrieval, about
thirty evaluation cases, export and import, and the memory centre. It was first drawn much smaller,
profile, events, deliberately dumb retrieval and ten cases, when the owner was going to write it by
hand.

**The only seam between them is the user id.** The brain takes it as an opaque parameter from
day one and never resolves it itself. The core issues the token, the brain trusts the verified
claim inside it. That is a ten line stub if the brain starts first, which is why neither track
is blocked.

**Core first**, not because the brain depends on it, since it barely does, but because the core
is small and genuinely finishable, it is the Rust and security practice that motivated the stack
in the first place, and doing it first means the brain gets a real user id from its first commit
instead of a stub to remove later.

The cost of that order, worth stating so it is not discovered later: the original reasoning put
the brain first deliberately, since that is where the design risk lives and reaching measurable
ground early is what makes the hard part tractable. Core first defers that. Acceptable, because
the delay is short at this scale and because the evaluation cases need no code at all and can be
written on paper while the core is in progress.

What this milestone does not include, each with the test it fails. Consolidation and the review
queue wait, because nothing can check a conclusion until features have sent real events. Calls
shaped for one feature, and the brain reading from features, wait for each feature. Search by
meaning waits, because a few dozen hand entered items give nothing to judge it against, and with it
any relevance score. So there are no per feature context calls to write: this builds the shared
retrieval building blocks, and the evaluation cases and the memory centre's own Ask page are their
first callers. Everything retrievable at this point is entered by hand, and events that features
send later accumulate without being concluded from. That is correct rather than a gap.

---

## Memory layers

Reduced from v1's seven to four:

1. **Profile**, identity, goals, values, interests, preferences and working style. Entered by
   the owner, moved only by the owner.
2. **Events**, append only record of what happened.
3. **Semantic**, claims and records the brain holds about the user, each linked to whatever it
   rests on. Most are derived from repeated evidence, but not all: the knowledge model's seed
   values, a decision's current status, and reported history are single sourced and live here
   too.
4. **Procedural**, versioned rules for how the system should behave.

Dropped: working memory, since it is workflow state and belongs to whatever runs the workflow;
graph memory, never built in v1; and document memory. Document memory was originally dropped on
the grounds that the wiki covers long form artifacts, and the wiki may never be built, but the
conclusion stands on its own. If a feature ever needs long form artifacts remembered, that gets
worked out then.

---

## Candidate features

The list of what might get built is not kept in this repository. It lives in the owner's planning
notes in Notion, one page per candidate, at the level of what he wants and why, and it stops
before design. A candidate arrives here when it is planned: its scope as a feature issue with its
slices, and whatever design outlasts it in these docs. Agents never need the list, since a slice
carries everything they build from. The same notes hold the parts of something built in several
rounds, which are built, which are left and what each waits for, and the order across the
platform; what exists is what the code and each service's README say.

It moved out because the two have different readers. The owner drops and weighs ideas where he
works through the day, and only agents work in the repository, so a list kept here was read by
nobody who could act on it. What stays here is what the platform is designed around whichever
features appear: a feature is whatever registers with the core and talks to the brain, as
`High-level/docs/feature-contract.md` says. Design written ahead of the building, such as
`desk/docs/desk-architecture.md` and `Brain/docs/`, stays here too, since agents build from it.

**Two things named as features are not features.** Reminders is a delivery capability with its
own service, above. A mobile application is a client surface over features that already exist,
and mobile itself is deferred.

---

## What we are not doing

Recorded so it does not get re proposed without new information. Things specific to one service
are in that service's folder.

**Designing for standalone extraction.** Covered above. The cost is accepted rather than
engineered around.

**Cross feature entity links in the brain.** The idea was that the brain would hold links of the
form "this thing in feature A is that thing in feature B", created by hand through a feature's
UI. A link like that is a join table rather than anything the brain knows about a person, and it
would live in the brain only because the brain is the one place two features can both reach,
which is the drift the brain exists to avoid. The real case arrived with desk, a session pointing
at a learning session, and it is answered without the brain: an address plus a snapshot, owned by
the feature holding the link. See "How features reach each other" in
`High-level/docs/feature-contract.md`.

**Sorting features into tiers.** There was a model with three of them, federated, embedded and
linked. Two were empty and the third was every feature, so it sorted nothing. Embedded meant a
feature living inside the brain's process and database, which is a written invitation to the
drift the brain exists to avoid. A feature is whatever registers with the core and talks to the
brain, and what sits inside it is its own business.

**The operating system framing.** Northstar OS was a name, not an architecture. Dropped as a
design metaphor.

**Pre assigning a language to every feature.** Covered under languages.

**Splitting memory ownership across two languages.** This was v1's actual failure: the design
said one language owned canonical memory and the other proposed candidates, and the boundary
leaked in practice. Avoided structurally in v2.
