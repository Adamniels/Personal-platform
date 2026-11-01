# The core

Status: exploratory. The walking skeleton is being built.

Last updated: 2026 10 08

The platform's infrastructure: the thing every feature is reached through, and where nothing
interesting lives. The platform wide thinking that places it is in
`High-level/docs/platform-architecture.md`.

This is the most fully specified part of the documentation, which is what you would expect of the
part that gets built first. What is still open is noted where it sits rather than collected into a
list.

---

## What the core owns

- **Authentication**, and the account record.
- **Identity at the account level.** The core owns who you are as an account, the brain owns who
  you are as a person.
- **Routing** to features, which is a reverse proxy, Caddy, configured by a file rather than code.
  It also puts everything behind one domain, which is what makes cookies and requests between the
  core and features simple. See routing below.
- **The feature registry**, meaning the list of what features exist, where they live, and what
  they offer.
- **Its own frontend**, meaning the login pages, the front page, and shared navigation.
- **Composing the front page**, meaning calling the display endpoints of whichever features you
  chose and assembling the result.
- **The public keys** that everything else verifies tokens with, served as a set at an
  endpoint.

## What the core grows into, and what stays out

The core changes as features arrive, and that is the workflow rather than a smell. Adding a
feature means three things outside the feature itself: a proxy entry, connecting it to the brain,
and deciding whether anything of it belongs on the front page, which means writing that feature's
piece of the page if it does. There is no rule against the core
changing, and there is no requirement that it never be down. Sessions survive a restart, since
tokens are verified locally rather than by calling back.

Two things still do not belong in it, for their own reasons rather than because a test says so.

**Domain data.** A general backend slowly absorbs every feature's data because it is always the
path of least resistance. Features own their data, the brain owns what it means, and the core
holds neither. Note that composing the front page does not breach this: a feature sends display
data for that moment, the core renders it and keeps nothing.

**Notification delivery.** It needs persistence and background scheduling, which is a different
runtime shape from a service that answers requests. Its own small service, see
`High-level/docs/platform-architecture.md`.

`projects` was considered for the core codebase and dropped, since it is simply a feature, and 
features stay out of core, if there are a lot smaller features that don't really deserve it's 
own feature we rather group them in a some kind of general feature later.

---

## Authentication

The core implements its own rather than adopting an identity provider or an off the shelf
framework. This is chosen with the tradeoff understood and it is not the general recommendation.
The usual advice against writing your own auth exists for multi tenant systems holding other
people's data at scale. Here the blast radius is a personal platform with a handful of invited
accounts, and the learning is a stated goal of the project rather than a side effect.

**The line while doing it: build the flows, never the primitives.** Registration, login,
sessions, token issuance and verification, refresh rotation, logout and password reset are
written by hand. Hashing, signing and randomness are library calls, always. Hand written crypto
is where hand written auth actually gets broken, and that part is firm.

**Registration is open but invite coded.** A single use code generated from a terminal command,
consumed by exactly one registration, and expiring. That keeps signup a real flow worth building
rather than a hardcoded bootstrap account, without leaving the platform open.

**An escape hatch, kept deliberately.** Token issuance and token verification stay behind
separate interfaces, and everything downstream only ever verifies, so an identity provider can
be dropped in later without touching the brain or any feature.

### The session

**Every service verifies for itself.** The core issues tokens and everything else verifies them
locally, with no call back to the core. Features and the brain read the user identity out of the
token and check the signature themselves. Nothing trusts a user id set by something upstream, and
the obligation that puts on a feature is in `High-level/docs/feature-contract.md`.

The alternative was verifying once at the edge and handing features a user id in a header. It was
dropped for two reasons. Routing is a config file doing path matching, so it cannot check a
signature, which means edge verification would need either a forward auth hook calling the core on
every request or every request routed through the core process, and the second makes the core a
hard dependency for every page load in the platform. That contradicts the tradeoff already
accepted for the nav, where the core being down costs a nav bar and nothing else. The other reason
is that a feature which believes a header holds no authentication logic at all, which is identity
arriving as a parameter wearing a different hat.

**The signing key is asymmetric**, an Ed25519 signed JWT. The core holds the private half and is
the only thing that can mint a token. Everyone else holds the public half and can only check one.
With a shared secret every verifier is also a minter, so any feature could issue a token for any
account, which is a strange property in a platform whose whole shape is features not trusting each
other. The core serves its public keys at an endpoint, so nothing hardcodes one or passes it around by
hand, and rotation stays a core only operation. RS256 is the fallback if a language picked
for some later feature has no Ed25519. PASETO was the alternative and lost on library coverage,
since most of the safety it adds is already bought by never hand writing primitives.

**The token carries a token id and a session id, and nothing reads them.** They are there so a
revocation mechanism can be added later without changing the format. Adding a claim afterwards
means updating every verifier and reissuing every live session; a claim that has always been there
simply starts being read one day.

**The session id is the family id**, not the id of the refresh token row. Rotation writes a new row
every thirty minutes, while the family lasts the whole session, and a family is what gets revoked:
by logout, reuse detection, a reset, or a password change. Changing a password is also likely the
first thing to read the claim, since it revokes every other session but keeps the current one, and
the refresh cookie that could identify the current one never reaches that endpoint.

**The session lives in two cookies.** The access token on `Path=/`, so it reaches the core and
every feature. The refresh token on `Path=/auth`, which holds refresh and logout and nothing else,
so it never reaches a feature at all. Logout is there rather than beside the other pages because it
revokes the family the refresh token belongs to, and so has to receive it, whether or not the access
token is still valid. Both `HttpOnly`, `Secure`, `SameSite=Lax`.

Cookies rather than a token held in JavaScript, because with everything on one origin the browser
attaches the session by itself, so a feature frontend needs no code to be authenticated, and
`HttpOnly` means a script that gets onto a page cannot read the token. A bearer token in
JavaScript is immune to CSRF but is lost outright to any XSS. CSRF is close to fully solvable and
token theft through XSS is not, which is the whole trade.

**CSRF is handled by `SameSite=Lax`, an `Origin` check on anything that changes state, and never
changing state on a GET.** No synchronizer tokens and no double submit, so there is no shared
state between the core and features. Accepting only JSON on mutations called by `fetch` closes the
same hole a second time and costs nothing. The core's own pages are the exception: login,
registration and the refresh page post plain forms, see the core's frontend below, and rely on
`SameSite` and the `Origin` check alone, which behave the same for a form as for a `fetch`.

**The refresh token is opaque rather than a JWT.** A random string, stored hashed, usable once.
Each refresh mints a new one and marks the old one used. Presenting an already used token means a
copy exists somewhere, so the whole session family is revoked and that person logs in again. This
is also what makes logout mean something and what lets sessions survive a restart, since the truth
is a row rather than a signature.

Nothing about a session is deleted in normal operation. Rotation marks a row used, and logout,
reuse detection, a password change and a reset mark it revoked. The used row is what makes reuse
detectable, so a `DELETE` anywhere in these flows quietly turns a replay into an unknown token.

**Marking a token used is one conditional update**, setting the used time only where it is still
empty and returning the row. Read first and write second, and two concurrent refreshes both find
the token unused and both rotate it, so the family forks and nothing ever notices. Invite codes and
reset tokens are consumed the same way, for the same reason: two registrations racing on one code
would otherwise both succeed, and a single use code would make two accounts.

**A token used within the last thirty seconds is not reuse.** Normal browsing presents the same
refresh token twice: two tabs reloaded after a break, or one page whose API calls all get a 401 at
once. Without a grace window the second of them revokes the family, and using the platform logs you
out. Inside the window the late request gets a fresh access token and nothing else, no rotation and
no new refresh cookie, because tabs share one cookie jar and whichever request won has already set
it. That keeps the outcome independent of which response arrives first. The cost is that a stolen
copy presented within thirty seconds of the owner's own use earns one access token without being
caught then. It cannot mint anything further, and if the thief got there first, the owner's next
refresh falls outside the window and revokes the family, so detection is delayed rather than lost.

**The access token lasts thirty minutes, and that number is the revocation window.** A locally
verified token cannot be called back on, so a logout or a stolen laptop stays live until it
expires. The refresh token dies the instant you act, so nothing new can be minted, and the worst
case is one already issued access token surviving half an hour. Fifteen minutes is the textbook
figure and is aimed at a stranger replaying a stolen token, which is not the threat on a platform
with a handful of invited accounts.

**The clock leeway on verification is chosen, not inherited, and it is five seconds everywhere.**
JWT libraries accept a token slightly past its expiry to absorb clock skew, and their defaults
differ: Rust's `jsonwebtoken` allows sixty seconds unless told otherwise, PyJWT none. Left at the
defaults, the core and a feature disagree about when the same token died, and the thirty minute
window above is quietly thirty one. Every verifier, the core included, sets the leeway explicitly
to the same value. Five seconds is small enough to leave the window meaning what it says and large
enough that services on different machines one day do not reject a fresh token over a clock
drifting by a second.

**The refresh token lasts fourteen days idle, and the session thirty days in total.** Each refresh
issues a token expiring fourteen days out, or at the family's thirty day cap if that comes first,
and rotation never moves the cap. The idle limit ends a session left alone. The cap is for the case
reuse detection cannot see: a copy is only caught when both copies get used, so a stolen laptop
whose owner simply logs in somewhere else leaves the stolen family alive for as long as the thief
keeps refreshing it. Changing the password revokes it, but only if the owner thinks to. The cap
is the backstop for when they do not, and costs one login per device a month. The refresh
cookie's lifetime matches the token's, so the browser drops it when the core would reject it
anyway.

**Expiry is not free for features.** An API call gets a 401 and the frontend refreshes and
retries, but a plain page load needs the feature's backend to redirect to the core and back. So a
feature holds signature verification and one redirect, which is little but is not nothing.

**The refresh endpoint is the one place that decides between refreshing and logging in.** A
feature sends every request without a valid access token there, whatever the reason, see
`High-level/docs/feature-contract.md`. A valid refresh cookie gets a rotation, both cookies set,
and a redirect back. Anything else goes to the login page carrying the same return path, and login
sends you there afterwards. The access cookie's lifetime matches its token, thirty minutes, and
logout clears both cookies as well as revoking the family.

**Refresh is always a POST.** A redirect arrives as a GET, and state never changes on a GET, since
that rule is what makes `SameSite=Lax` enough. So the GET on the refresh endpoint only renders a
small page whose form posts to the same path at once, with one line of inline script and a visible
button if scripts are off. The POST passes the same `Origin` check as everything else, and it is
the same request a frontend's `fetch` makes after a 401, so refresh has one behaviour however it is
reached. It costs a round trip at most once every thirty minutes. Keeping refresh as a GET would
have been defensible, since a forced refresh only rotates your tokens inside your own browser, but
a rule with one justified exception is how the next one arrives.

**A return path is only ever a relative path**, starting with a single `/`. An absolute URL or one
starting with `//` is dropped and replaced with the front page. Without that, the login page is an
open redirect: a genuine login link that lands you on someone else's site afterwards.

### The signing key

The private key signs every token in the platform. If it leaks, anyone can mint a token for any
account and everything above is void, so it is handled differently from other configuration.

**It never goes in the repository, not even in development.** A development key committed for now
is how a production key ends up committed later, because the file already exists and someone fills
it in. Configuration arrives as environment variables, loaded from a file that is not tracked,
with a tracked example listing the names and no values.

A fixed seed inside the core's test code is not an exception to this. The token contract test
signs with one so the output is the same bytes every time, see contract fixtures in
`High-level/docs/platform-architecture.md`, but it is a constant in a test rather than a key file,
the core never loads a key from anywhere but the environment, and nothing it signs verifies against
a real key set.

**Every environment generates its own**, from a small command in the core that emits a keypair. A
development key must never verify anything real.

**The token header carries a key id, and the endpoint serves a set rather than one key.** Both are
unused while there is a single key. They are what makes rotation possible later without a flag
day: publish the new key, start signing with it, keep verifying the old one until everything signed
with it has expired, then drop it. Without them, rotating means every live token becoming invalid
at once and every verifier being restarted in the right order. This is the same trade as the token
id above, free now and expensive to retrofit.

**No pepper on the password hash.** It adds a second secret to protect in exchange for very little
on top of Argon2id.

### Password reset, and changing a password

Reset works like registration: a command generates a single use token and no email is involved.
It prints the token, you paste it into the reset page, you set a new password. Delivery is the
only missing piece, and delivery is exactly the piece that belongs to the notification service, so
when that exists the flow does not change. The core sends a delivery request instead of printing.

Sending mail from the core is ruled out rather than deferred. It is the drift the notification
decision exists to prevent, and it would pull a provider, deliverability and templates into a
service holding one account.

**Consuming a reset token revokes every session for that account.** A reset happens because
someone may have the password, so a session that survives it defeats the whole point. This is also
where the thirty minute window is least comfortable: nothing new can be minted, but an access
token already in someone's hands keeps working until it expires. It does not change the answer at
this size, since an attacker would have to be inside the account during that particular half hour,
but it is the scenario that comes closest to justifying the revocation feed under what we are not
doing.

**The token is short lived, an hour rather than a day**, stored hashed, usable once. That is the
invite code machinery again rather than anything new. Using it does not log you in: it sets the
password and sends you to the login page, because letting a reset token turn into a session adds a
second path by which something becomes a session, and that is a category worth keeping to exactly
one.

**Changing a password while logged in is a separate flow.** It requires the current password, and
it revokes your other sessions but not the one you are sitting in. It is also the one you will
actually use.

**No defence against user enumeration, deliberately.** A public forgot password form has to answer
identically whether or not an address exists, or it becomes a way to find out who has an account.
There is no such form here and the command is run by the owner, so the concern does not arise.
Worth writing down, because it is a reflex that would otherwise be carried forever without anyone
remembering the reason.

This is about reset only. Login is a public form, so it does the cheap thing: the same answer for an
unknown email and a wrong password, and an Argon2id verification against a dummy hash when the
email is unknown, so the timing does not tell them apart either.

---

## The core's data

Four tables, and nothing else.

`accounts` holds the id, the email, the password hash, a status, and when it was created.
`invite_codes` holds the code hashed, when it was created, when it expires, when it was used and
by which account. `sessions` is the refresh side of the token model: id, account id, family id,
token hash, issued at, expires at, family expires at, used at, revoked at. The family expiry is set
at login and copied forward on each rotation, so a refresh can respect the cap by reading one row. `password_reset_tokens` has the same shape
as invite codes, since reset is the invite code machinery again, see password reset above.

Invite codes, reset tokens and refresh tokens are stored hashed. Each of them is a credential, and
a database backup should not be a working set of them. All three are plain random bytes rather
than UUIDs, since a UUIDv7 carries its own creation time and a secret should carry nothing.

They are hashed with SHA-256, not Argon2id. A slow hash exists to make guessing expensive, which
matters for a password a person chose and not for thirty two random bytes, which cannot be guessed
either way. And each of these is found by its hash, so the hash has to be deterministic and cheap,
where a salted slow hash would mean no index to look it up by.

Passwords are hashed with Argon2id through a library, per the line above about primitives.

Email is normalised to lowercase and unique on that. Treating two spellings of one address as two
accounts is a bug that first appears on a phone keyboard that capitalises.

Nothing records a user agent or an IP address on a session yet. That would be wanted the day
there is a device list to render, and adding a column to a table with three rows in it costs
nothing, which is exactly what the next section is not.

### The user id

The account id is a UUIDv7, minted by the core and by nothing else.

It is the hardest value in the platform to change, and that is worth understanding rather than
taking on faith. It is not stored in one place. It is copied into the brain's events and claims,
into every feature's own tables, and into every token in flight, across databases that cannot be
joined and are never migrated together. Any other column is one `ALTER TABLE` away from being
different. This one has no migration at all, because no service holds both the old value and the
new one, and the brain's event log is append only, so rewriting an id in it contradicts the thing
the log is for.

**Not a counter.** Sequential integers are fine inside one database and dangerous the moment the
value leaves it. Account five in a development database is a different person from account five in
production, so restoring a partial backup, seeding a test environment, or importing anything from
v1 can attach one person's history to another account with no error raised. The whole isolation
story rests on this value being right, so it should be impossible to confuse rather than merely
unlikely.

**Not the email either.** A key should identify and nothing more. Email changes, and then either
every copy gets rewritten or the key quietly lies. It also copies personal data into every table
in every service, which is a strange thing to do deliberately in a system built on isolation.

**Version 7 rather than 4**, for index locality. A random key lands in a random page of a B tree
on every insert, so pages split half full and the working set becomes the whole index instead of
its hot end. A time ordered key lands at the right hand edge like a counter does. Nobody notices
this on a small table, and the brain's event table is append only and grows forever, which is the
one place it bites. The cost of v7 is that it leaks its own creation time, which is meaningless
for an account and is why secrets are not UUIDs.

**An identity provider maps to this id rather than replacing it.** If the escape hatch above is
ever used, the provider arrives with its own subject identifier, and in the moment it will look
simpler to let that become the user id. It is not: an external id reaching the brain means
rewriting the append only log. The core translates at the edge, and everything downstream keeps
storing what it was handed without interpreting it.

### Row level security does not apply here

The platform enforces user isolation in the datastore rather than in query discipline, and
`High-level/docs/platform-architecture.md` has the reasoning and the four conditions that make it
real. The core is outside that rule.

Look at what its queries are. Login finds an account by email, run by someone who has not yet
proved they are anyone. Refresh finds a session by its token hash, located by the secret rather
than by its owner. Redeeming an invite code is the same shape. There is no user id to put in the
transaction scoped setting at the moment any of them run, because producing that user id is the
entire job. Switching policies on would make login return zero rows, so it would have to be
carved back out with an unauthenticated role permitting exactly those reads, which is ceremony
protecting nothing.

The deeper reason is that row level security earns its cost where the query surface is large,
growing and written over years, which is the brain exactly. The core is a fixed handful of queries
in one small codebase, and most of them cannot be user scoped even in principle. A safety
mechanism placed where the failure mode is not becomes decoration.

Two halves of the discipline are kept anyway, because they cost nothing and stand on their own:
the application connects as a role that does not own its tables, and migrations run as the owner.

**The running core holds only the app role's connection string.** The owner's is never in the
core's environment file, because the binary loads that file on every run, the server's included,
and a core holding the owner's credentials has the role split only on paper. The `migrate`
subcommand reads the owner's connection string from a variable of its own, set in the terminal for
that one command. sqlx's query macros are pointed at the app's variable, so they check queries as
the app role.

The tests hold a third, the Postgres superuser's, in `CORE_TEST_ADMIN_DATABASE_URL`, used only to
create a database per test from a migrated template. It follows the owner's rule: never in the
environment file, set in the terminal or on the one CI step that runs the tests. Each test still
connects to its database as the app role, so it tests what the running core is, see
`Core/tests/common/mod.rs`.

**The application role is granted no `DELETE`.** Nothing in the core deletes in normal operation,
see the session above, and a `DELETE` that slipped into a session flow would turn a replay into an
unknown token without raising anything. Without the grant it is a permission error instead.
`TRUNCATE` is withheld too, since it empties a table without needing `DELETE`. The day something
genuinely needs deleting, account deletion or pruning old sessions, it gets a grant in a migration
of its own.

---

## Routing

Caddy, configured by a Caddyfile that sits next to the feature registry and changes in the same
commit.

Chosen over Traefik because what Traefik is built for is not what this is. Its strength is
discovering routes from whatever is running, through Docker labels or Kubernetes, and here the
services run natively and the registry is static by decision, so it would be used through its file
provider, which is the Caddyfile in more YAML. The local certificate authority that
`High-level/docs/platform-architecture.md` asks for is built into Caddy and would be a second tool
alongside Traefik. nginx has neither. Worth revisiting if the platform ever moves to containers
that come and go, and cheap to be wrong about, since the proxy is a handful of routes in one file.
Trusting Caddy's local root on the laptop is a manual step, done once.

Two rules come with it.

**The proxy is for browsers, and services call each other directly.** The core calls a display
endpoint at the upstream address in the registry, and a feature fetches the public keys from the
core's own address, given to it as configuration. Going through the proxy would mean every client
library trusting Caddy's local root, and the day one forgot, it would surface as a TLS error in
some other language's HTTP client.

**The path prefix is kept, not stripped.** A feature receives `/canary/...` rather than `/...`, so
its links and redirects are right without it knowing a proxy exists. Nothing in a feature depends
on anything Caddy specific, which is what keeps changing proxies a config file rather than a
migration.

---

## The feature registry

A config file the core reads at startup, not a database table.

The deciding argument is that routing is already a config file for the proxy. If the registry were
a table, adding a feature would mean editing a file and inserting a row, and those two could
disagree about the same fact, which is a class of bug with no upside whatsoever. The two files sit
in the same directory and change in the same commit.

The rest follows. The registry holds no user data and has no user scope, so it would be a strange
guest in a database that is otherwise entirely per account. Nothing edits it while the system is
running, since there is no interface for registering a feature and there is not going to be one.
And a table quietly invites a registration endpoint, which invites features announcing themselves
at boot, which is a distributed systems problem that does not otherwise exist here.

Four fields per feature are enough to build against: display name, path prefix, upstream address,
and the display endpoint if it has one. The fuller manifest, a read API version, the entity types
a feature exposes, the scopes it requests, has no consumer yet. It belongs to the brain and to a
platform with several features, and writing it before either exists would be guessing.

The cost is that adding or disabling a feature needs a restart, which is consistent with the core
changing as features arrive being the workflow rather than a smell.

---

## The core's frontend

The core renders its own HTML, from a template engine in Rust. No Node in the core, no build
step, no second process. The pages are the login and registration flows, the front page, and
whatever account screens arrive later.

**Why not a TypeScript application, when feature frontends are expected to be one.** Server
composition is the reason. An application cannot serve a composed page in one response: it loads
a shell, then asks, and the composition either moves into the browser or becomes a JSON endpoint
rendered after a second round trip. Rendering on the server is what the front page's whole design
rests on, so the core's frontend is the one place the platform's frontend language does not apply.
Feature frontends are unaffected.

Two things come with that and both are worth having on their own. Login is a plain form post, so
the most security sensitive surface in the platform works with no JavaScript at all and carries
the least code it can. And the `Origin` check that handles CSRF behaves identically for a form
post and a fetch, so there is one mechanism rather than two.

**The nav script is used on the core's own pages too**, rather than the nav being rendered into
the templates. The core has the feature list in hand and could render it directly, but then the
bar exists in two implementations and they drift.

**Styling has no build step either**, just a stylesheet the core serves. If a design system ever
arrives it should arrive because that cost was chosen deliberately, and
`High-level/docs/platform-architecture.md` names it as the largest permanent cost in the plan.

**Where this stops being the right tool**, written down now so it is recognised rather than
discovered. It holds while the front page is something you read and click through. The moment you
want to act on a card without leaving it, mark something done, edit a value inline, watch a number
change, you are building an application and templates start to hurt. There are three stages and
each is cheap to enter the day it is needed: templates with plain form posts, which covers more
than it sounds like; htmx, one script tag and still no build step, which is the natural next step
for a page rendered on the server; then a real framework if the front page becomes an application
in its own right. Unlike the user id this is cheap to be wrong about, since the front page is one
page in one service.

---

## What a feature exposes for the front page

Optional, per feature, and not a contract. When you build a feature you decide whether anything
of it belongs on the front page. If it does, it exposes an endpoint returning display data, and
the core calls it with your token forwarded.

It returns data, not rendered output. The core decides how it looks, which is what keeps the page
coherent rather than a patchwork, and what keeps the endpoint useful to anything else that might
want to read it later.

Nothing is required. A feature with nothing worth showing exposes nothing, and the registry
records which features have one and where.

**There is no shared display shape, and there is not going to be one.** Each feature exposes
whatever its own data naturally is, and the core gets rendering written for that feature
specifically. Putting a feature on the front page means editing the core: a call, a type for what
comes back, and a piece of template. That is the intended workflow, not a cost being tolerated.

```json
{ "active": 4, "due_today": 2, "next": "Finish the invite flow" }
```

That is what desk happens to send. It is not a format. The next feature sends something else,
shaped by what it actually has, and the core learns to render that too.

Each feature's shape is still a contract between two languages, one feature at a time, so a feature
on the front page commits a fixture of its data with the contract fixtures in
`High-level/docs/platform-architecture.md`. The feature's tests check its real response against it
and the core's tests read it into the type the card renders, so drift fails a test rather than
blanking a card.

The alternative is a common vocabulary every feature conforms to, some arrangement of titles and
counters and lists, so that the core can draw anything without being touched. It is the same idea
as the generic search envelope in `High-level/docs/feature-contract.md`, and it is rejected for
the same reason: flattening every domain into one shape loses exactly the structure that made the
thing worth putting on the page, and the abstraction buys nothing except not editing a codebase
that is one person's and sitting right there.

What that costs, plainly: the front page grows with the number of features rather than staying
fixed, so each one adds a small amount of permanent code to the core. At five or ten features that
is a handful of functions. It would be the wrong trade in a system with a hundred of them.

**The line that keeps this honest is that a feature sends data about its own domain, never
presentation.** The moment a payload carries a colour, a width, an ordering, or a string meant to
be shown verbatim, the feature is remote controlling the core's page and the whole reason for
sending data rather than rendered output is gone. The core reads domain data and decides
everything about how it looks.

**The calls are concurrent and have a deadline**, on the order of five hundred milliseconds each.
Without one, a down feature costing its piece rather than the page is not actually true. A feature
that misses the deadline gets a visible placeholder saying it could not be reached, rather than
disappearing, because a card that silently vanishes is how something stays broken for a week
without being noticed.

**The core calls with the user's token in an `Authorization` header**, since it has no cookie jar
of its own. The same endpoint receives it in a cookie when a browser asks. A feature accepts
either, see `High-level/docs/feature-contract.md`.


---

## Libraries the core is built on

**axum for HTTP**, on tokio. Chosen because it is built on tower rather than its own middleware
system: the hand written auth, session checks and token handling, becomes ordinary tower layers
and extractors, and `tower-http` covers the generic ones. Handlers are plain async functions, with
little macro machinery hiding the request pipeline. It pairs naturally with sqlx on the same
runtime and with the Rust template engines for the core's own HTML. Actix-web was the close
alternative, equally mature and marginally faster, which does not matter at this scale, but with
its own middleware model. Rocket's batteries, mainly sessions and auth helpers, are the part this
project writes by hand anyway. Cheap to reconsider now, expensive once auth middleware exists.

**sqlx for Postgres**, on the same tokio runtime. Chosen because it is a driver, a pool and a
migration runner in one crate without being an ORM: every query is plain SQL, which the core's
small and security critical set of queries wants visible. Its `query!` macros are used from the
first query. They check each one against a real database at compile time, so a wrong column or
type is a build error rather than a login failing at runtime. They cannot see permissions, so a
query the app role is not granted still compiles and fails when it runs, which is where the grants
are meant to bite. Its migrations are plain numbered SQL files, as
`High-level/docs/platform-architecture.md` requires, embedded in the core binary and applied by
its `migrate` subcommand as the owner. Its documented habit of migrating at startup is not
followed, because the app role cannot migrate. `sqlx-cli` is used only to write the macros'
offline cache, `.sqlx/`, which is committed so the core builds without a running database.
tokio-postgres was the close alternative, nearer the protocol, but it needs a pool and a migration
runner added and checks nothing before runtime. Diesel puts a DSL where the SQL should be and is
synchronous at its core, and SeaORM writes migrations in a DSL, which the platform rules out.

**Rust itself is pinned**, in `Core/rust-toolchain.toml`, so the laptop, the Mac mini and
continuous integration build with the same compiler, and a new stable release cannot turn clippy
red on a push that changed nothing. Moving to a newer Rust is a commit of its own.

---

## What we are not doing

Recorded so it does not get re proposed without new information.

**Letting `nav.js` refresh the session in the background.** It would make token expiry genuinely
invisible to features, since every feature already includes the script. It is not done because the
nav rendering and linking and doing nothing else is what keeps pulling a feature out to deleting
two lines, and an auth helper riding along is exactly the drift that rule exists to prevent. See
the nav in `High-level/docs/platform-architecture.md`.

**Audience scoped tokens, one per feature.** One cookie on the whole domain means every feature
backend receives a credential that would also work against every other feature, so features not
calling each other is a topology rule rather than something cryptography enforces. Per feature
tokens are the real answer and are where the scopes in the registry manifest eventually lead. Not
built now, because it is a token exchange mechanism to design and every feature is the owner's own
code.

**A revocation feed, so a session can be killed instantly.** Every version of this works the same
way: the verifier has to learn something the token cannot tell it, so it is either a lookup on
every request or a list the core publishes and each service caches. The cheapest shape is a
session epoch per account, one integer, bumped to invalidate everything that account holds. It is
not built because thirty minutes is an acceptable window here, and because the token already
carries the identifiers that would make adding it later a small piece of work rather than a
migration. Whatever is built has to serve stale on a core outage, since failing closed would mean
the core going down logs the whole platform out. The case that comes closest to justifying it is
password reset, and the reasoning for that sits with reset rather than here.

---

## What shapes the core from elsewhere

All in `High-level/docs/platform-architecture.md`:

- The core is Rust, and why.
- The core track goes first, and what is in it.
- Multi user from the start, and isolation enforced by the datastore rather than by query
  discipline. That binds the accounts schema and how the core connects to it.
- Notification delivery is its own service, not this one.
- Profile lives in the brain, which fixes the account versus person line.

And in `High-level/docs/feature-contract.md`: both directions of the two contracts carry a
verified user identity, which the core is what issues.
