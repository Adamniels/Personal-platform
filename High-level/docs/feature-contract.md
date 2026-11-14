# What a feature is, and what it may depend on

Status: exploratory. Nothing is built.

Last updated: 2026 11 14

Isolation of implementation, coupling through contracts. This is what someone building a feature
needs to know. The brain's own internals are in `Brain/docs/`, and the platform wide thinking
these sit inside is in `High-level/docs/platform-architecture.md`.

---

## What a feature is

A feature is whatever registers itself with the core and talks to the brain through the two
contracts below. It owns its data and its frontend.

What sits inside it is its own business, whether that is one thing or several smaller ones
bundled under a general name. Either shape looks identical from the core's and the brain's side,
so there is nothing to standardise and nothing to declare.

A feature may also expose an endpoint with display data for the front page, described in
`Core/docs/core-architecture.md`, and may include the shared nav bar, described in "Navigation" in
`High-level/docs/platform-architecture.md`. Both are optional and neither is a contract.

## Features own their data

A wiki concept node belongs to the wiki. A learning session belongs to learning. The brain never
holds a copy of any of it.

**A feature sends the brain something once, and that is the whole exchange.** What it sends is
what the brain should learn from that feature, which by the filter in
`Brain/docs/knowledge-model.md` means the things that change an output outside that feature. It
is always a small, deliberately chosen set rather than a feed of everything that happened.

**The evidence comes with it, as readable text that stands on its own.** "Worked through B tree
internals, got the page split case wrong twice, said the material was too basic" is something you
can read and judge years later without asking anything else. Every event carries one, in a field
of its own, see the event shape in `Brain/docs/event-model.md`. The brain never holds a pointer
into a feature and follows it later to find out what it meant.

That matters more than it looks. The trust model rests on "why do you believe this about me"
always being answerable. A pointer makes that conditional on the feature still being up and the
row still existing, so deleting a session would quietly leave the brain holding a claim it can no
longer justify. Self contained evidence makes the answer unconditional, and it is also what keeps
the promise that nothing in the brain is ever destroyed, since a feature cannot destroy it by
deleting its own data.

The cost, worth being clear about: write time is the only chance. Whatever a feature leaves out
of that payload is gone, because an event records a moment and cannot be backfilled. Payloads
should err generous.

The brain embeds what it holds, so memory becomes semantically retrievable. It does not embed
feature content, because it does not have any.

The brain owns what that data means about the person, and that is the whole division.

## How features reach each other

Four different needs cross a feature boundary, and each has a path of its own. They used to be
one rule, all cross feature access goes through the brain, and that was too broad. Routing a
lookup of one specific record through the brain removes no coupling. The asking feature still
depends on the shape of what comes back, the brain now depends on it too, and the brain has
learned a piece of another feature's domain.

**Knowledge about the owner goes through the brain.** What the owner already knows about B trees,
what has gone wrong lately, what they are working towards. A feature asks one place in one
vocabulary and never learns which features exist or how they store things. The brain earns that
by translating into its own model, not by forwarding. A brain that hands a feature's records
across unchanged is a proxy, and a proxy leaves every coupling in place.

**Display and navigation are composed in the browser.** A button in a desk session that opens a
learning session is a link. If desk wants that session's title beside the link, desk's frontend
fetches it from learning with the owner's own token. That is the user holding their own token,
not a feature calling another. A failure stays local: the piece showing learning's data says it
is unavailable and the rest of desk works. Where a feature lives comes from the registry.

**A reference to another feature's record is an address plus a snapshot**, owned by the feature
that holds it. Desk keeps `/learning/sessions/123` together with "B tree internals", the title
at the moment of linking. The address is a path rather than a full URL, so the hostname can
change. The target may move or disappear, and then the link fails while the snapshot stays
readable. Nothing is kept in sync and nothing else holds the link. It is the brain's reasoning
about self contained evidence, applied between features. How the holding feature gets the
address in the first place, pasted by the owner, picked from a list the browser fetches, or
handed over by a link from the other feature, is decided with the first real case.

**A backend may call another feature's backend, under four conditions.** This used to be a ban
with exceptions argued one at a time. It was turned around on 2026 11 12, when the features after
the core were laid out and several of them plainly needed each other: review takes cards from
sessions, learning and news, and a code project shows what the development flow knows. What the
ban protected is kept by the conditions instead.

1. **Through a published contract.** The callee describes the API in a schema, and the caller
   uses a client generated from it, with a fixture under `contracts/` that both sides test
   against, as `contracts/README.md` describes for the token. A feature never calls an endpoint
   the other has not published for it.
2. **In a declared direction, and never in a circle.** If A calls B, B does not call A, directly
   or through others. Calls that form a cycle are how services end up starting, deploying and
   failing together, a distributed monolith. Every allowed direction is listed under "Declared
   calls" below, and adding one is a decision made in planning.
3. **The caller keeps working when the callee is down.** Every call has a timeout, and the part of
   the page or job that needed the answer says it is unavailable while the rest goes on. An
   experimental feature must not be able to take down one that is used every day.
4. **Each feature still owns its data.** Nobody reads another feature's database, and what one
   feature keeps about another's record is still an address plus a snapshot, as above.

**Being allowed to call is not a reason to split.** A boundary costs a network call that can fail
and data that has to be kept consistent across two stores, so it belongs where two things change
independently and reach each other now and then. Things that need each other to draw every page
are one feature, with modules inside it. A session cannot be shown without its project, so
sessions and projects are one feature; a review card only needs a link back to where it came
from, so review can be its own.

The brain and the browser remain the first choice where they fit: knowledge about the owner goes
through the brain, and showing another feature's data beside your own is often simplest in the
browser. A backend call is for when the backend itself needs the answer.

### Declared calls

None yet, since no feature that calls another is built. Each one is added here when its feature is
planned, as caller, callee, what is asked for, and what the caller does without an answer.

Nothing enforces any of this cryptographically today. The session is one cookie valid at every
feature, so a feature backend receives a credential it could replay against any other one. Per
feature tokens are the answer if that ever stops being acceptable, and
`Core/docs/core-architecture.md` covers why they are not built yet. If they come, they have to
keep composition in the browser working.

## The brain does not connect to feature databases

A feature exposes a narrow read API over its own domain, and the brain calls it. The feature
keeps fast local access to its own database, and cross feature access is one extra hop through
the brain, which is milliseconds and fine.

The alternative, the brain holding direct database connections to every feature, was considered
and dropped. It couples the brain to every feature's internal schema, breaks when a feature
refactors, bypasses the feature's own rules so the brain reimplements them badly, and spreads
credentials around.

## The brain stays out of a feature's domain

It routes, merges, ranks, and remembers. It does not decide what makes a good wiki node, or when
a project task is blocked. Ranking and merging are its own domain, so those are not violations.

Nothing structural enforces this. It is judgment, applied each time a call gets added, and the
place it will go wrong if unwatched is a context call that starts returning advice instead of
facts.

## Brain access sits behind one port per feature

One adapter, not scattered HTTP calls through the codebase.

Worth being clear about why, because the obvious reason is the wrong one. This is not for
portability or future extraction. It is for testability, so a feature can run against a fake
brain in its own test suite, and so the implementation can be swapped without touching callers.

## How finely a feature observes is up to it

Not a global rule. It depends on what the feature does and what the brain should learn from it,
so it gets decided when that feature is designed. The brain owns the verb vocabulary, so
anything emitted can be weighed, and the feature owns how closely it looks.

One thing ruled out everywhere: interaction level tracking, scroll position and time on screen.
It needs instrumentation in every corner and time on screen is a bad proxy for anything.

## Records apply immediately, claims wait

Consolidation exists to infer claims from behaviour. When the owner records a fact there is
nothing to infer, so it does not wait.

- **Records write immediately.** A profile edit, "I took that course in 2023", "this session was
  too basic". Each states a fact, so there is nothing to get wrong.
- **Exposure, engagement and activity are evidence.** Generalisations from them wait.
- **Features write records directly, and never write claims directly.** A claim about the person
  goes through evidence and consolidation.

Writing immediately does not mean skipping consolidation. Anything written directly is still
picked up on the next pass to be linked, merged, or checked for contradiction. Immediate effect
and later integration are separate things and both happen.

One thing worth not getting wrong here: "talk to me more concisely", said mid conversation, is
not a record. It is a claim about the person wearing an imperative, and letting one sentence
create a standing global rule is generalising from a single data point, which the rest of the
design refuses to do everywhere else. `Brain/docs/trust-model.md` covers where those go instead.

## Reminders are delivery, not a feature

A feature sends a request: tell the user this, at this time or under this condition. Nothing
gets linked. Self authored reminders, "call the dentist", "look at the Q3 numbers on Friday", go
through the same service with the user as the source instead of a feature.

**A reminder is never read as a signal about the person.** It is not evidence, it is not a
preference, and it never influences ranking, generation or any other output. It is a note the
user wants delivered back to himself at a time.

Two things follow. The notification service does not write to the brain, since a reminder
changes no output and so it is a note rather than memory, and delivery flows one way. And
reminders are not standing rules: "use TDD" and "plan before prompting" are rules about how the
system should behave and belong to procedural memory, while a reminder is delivered and then
done.

`High-level/docs/platform-architecture.md` covers where the delivery service lives.

---

## How a feature authenticates

A feature verifies the user's token itself. It never takes the user id from a header set by
something upstream, and it never takes it from a parameter. Three things, and this is the whole of
it:

- Fetch the core's public keys from the endpoint that serves them, cache the set, and verify the
  signature on the access token, choosing the key by the id in its header. An id the cached set
  does not know means fetching the set once more before failing, which is what lets the core rotate
  keys without every feature rejecting fresh tokens. Refetch at most once every thirty seconds,
  or a stream of made up key ids turns every request into a call to the core. The token arrives in a cookie when a browser is
  asking, and in an `Authorization` header when the core is calling a display endpoint on the
  user's behalf, so accept both. Set the clock leeway explicitly rather than taking the library's
  default, see `Core/docs/core-architecture.md` for the value and why. The identity is whatever the
  verified token says.
- On anything that changes state, check that the `Origin` header is the platform's own, and never
  change state on a GET. That is the CSRF defence in full.
- Treat a missing token, an expired one and one that fails verification the same way. An API call
  returns 401 and the frontend refreshes and retries; a plain page load redirects to the core's
  refresh endpoint with the path to come back to. The feature never decides whether someone needs
  to log in, since it cannot tell a logged out browser from one whose access cookie simply ran
  out, and the core can.

So a feature frontend needing no authentication code is not quite true. It needs none to be
authenticated, since the browser attaches the session cookie by itself on a shared origin, but the
feature holds verification, an origin check, and one redirect. Everything else, issuing,
refreshing, rotation, the cookies and the key, belongs to the core and is described in
`Core/docs/core-architecture.md`.

A Python feature installs all three rather than writing them: `platform-auth`, the library in
`libs/python/platform-auth/`, holds where the token is read and what a request without a valid one
gets, the verification, the key cache and the `Origin` check, and its README says how a service
installs it. A feature no longer copies canary's files; canary and the brain install the library
too. A C# feature does the same with `PlatformAuth`, in `libs/dotnet/PlatformAuth/`, which holds
the same three as an ASP.NET Core authentication handler with two policies, one per kind of route,
and `Origin` middleware; its README says how a service references it.

**One implementation per language**, not a copy per feature, because this is the platform's security
code and its flaws are silent: a hole found and fixed in one copy stays open in every other, and
nothing shows it. The cost is that a change to the library reaches every service in that language
at once, which is right only while the library holds exactly these three things and nothing else,
the exception and its limit written in "One repository" in
`High-level/docs/platform-architecture.md`. A feature in another language gets that language's own
library when the first such feature arrives, tested against the same token fixture in
`contracts/token/`, so the implementations agree with the core and with each other. Python and C#
have theirs.

## The two contracts

Both stay small. If either drifts large, something has gone wrong.

**Downward, what the brain offers a feature**

- a named context call per feature, shaped when that feature is built
- get profile snapshot
- search, meaning search memory, not search everything the platform knows: by words for now,
  `GET /brain/api/search`, its query and answers in `contracts/brain/openapi.json` and described
  in "What it serves" in `Brain/README.md`; search by meaning waits until there is enough memory
  to judge it against
- record event, `POST /brain/api/events`, its body and answers in `contracts/brain/openapi.json`
  and described in "What it serves" in `Brain/README.md`
- propose memory candidate

The first entry used to read "get context for a task", which was close enough to v1's rejected
single magic call to be the same mistake under a new name. `Brain/docs/brain-architecture.md`
has how per feature calls work instead.

**Upward, what a feature offers the brain**

- a narrow read API over its own domain, shaped per feature
- the events it emits

Only the brain calls a feature's read API, which is a different thing from the API a feature's
own frontend uses. The browser may call the second from another feature's page. Another feature's
backend calls neither: it calls what the feature has published for it, under the conditions in
"How features reach each other". That is a topology rule rather than a security one.

**Both directions carry a verified user identity, taken from the token rather than from a
parameter.** This one is firm, and what goes wrong without it is not a wrong answer but one
person's data reaching another account. There is no unscoped call in either contract.

---

## What we are not doing

**A generic search envelope every feature conforms to.** Designed to avoid touching the brain
when adding a feature. At five features that cost is not real, and flattening every domain into
one shape loses structure that matters. Speculative abstraction.

**The brain connecting directly to feature databases.** Covered above.
