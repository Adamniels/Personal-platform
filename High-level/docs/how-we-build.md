# How we build

Status: exploratory. Nothing is built.

Last updated: 2026 09 21

How work gets cut up and ordered, as opposed to what gets built. What a particular sprint must
prove, and the tickets that get it there, live on the GitHub Project rather than here, see
`docs/README.md`.

Everything below was decided while planning the core sprint and argued against that sprint's
checks. It is written as platform wide because nothing in it is specific to the core, but it has
not yet met the brain. Planning the brain track is where it gets re examined, and if the brain
needs something different, that goes here as a difference with its reason rather than as a
second copy in `Brain/docs/`.

---

## A walking skeleton, then vertical slices

**Work is cut vertically.** A slice goes through every tier it touches, storage, logic, HTTP and
the page, and ends in something that can be used and checked by hand. It is not cut by layer,
building the schema, then the logic, then the endpoints, then the pages.

**When the pieces have never been connected, the first piece of work is a walking skeleton**: the
thinnest path through every moving part, end to end, with no behaviour in it. The proxy on the real
hostname with real HTTPS, each service reachable through it, the database with its roles and the
migrations command working. It holds no authentication and no domain code.

**Slices are ordered by risk, not by the order a sprint's checks happen to be listed in.** The
claim most likely to be wrong gets tested first, while little is built on top of it.

**A sprint's checks are acceptance criteria attached to slices, not a list of tickets.** Most
checks are a behaviour one slice delivers. Some are properties that fall out of the design, such as
staying logged in across a restart, and those get attached to the slice that makes them true
rather than becoming work of their own.

**Checks are walked by hand, and never turned into automated tests.** Each one is walked when its
slice lands, as part of that slice being done, and then all of them are walked again together at
the end of the sprint, on a fresh start of the whole stack, as the sprint being done. The final
walk exists to catch a later slice breaking an earlier one. Walking them only at the end would
find a problem from the first week in the last one.

### Why not layers

Layers buy two things. The first is designing each tier coherently before code accumulates on top
of it, and the documentation already does that: the tables, the token format and the cookie model
are decided before a line is written, so a layer phase would pay for a benefit already held. The
second is people working in parallel on different tiers, which does not exist with one person.

What layers cost is real. Every check is an end to end behaviour, so none of them passes until the
last layer lands, and the integration claims, which are the least tested ones, get tested last.
The core sprint's clearest case is a feature in another language verifying the core's token, which
is the reason `canary` exists at all, see the first milestone in
`High-level/docs/platform-architecture.md`. Layers would find out whether that works at the very
end.

### Why a skeleton rather than slices alone

Without one, the first slice silently absorbs all the plumbing, so a ticket named after a small
behaviour turns out to be the whole infrastructure, and nothing is visible when it is done. The
skeleton makes that work its own piece with its own finish line, and it retires the integration
risks, TLS, routing, several languages, database roles, before any logic depends on them.

### The rules that keep it honest

**The skeleton holds nothing a later slice would be the first to need.** Its known failure is
growing into a layer phase in disguise, the whole schema, an error handling framework, a config
system, because it is there and it is early. Anything with a slice to own it waits for that slice.

**A table arrives with the first slice that needs it, in its documented final shape.** The shape is
already designed, so building it incrementally would only add migrations, and forward only
migrations make that churn permanent.

**Each slice carries its own tests.** Tests are not a later pass over finished slices. Which
tests, see what gets tests below.

### What it costs

The skeleton is the one piece of work that no check stands behind, so its done condition has to be
written as concretely as a check would be, or it has no finish line.

Security logic lands across several slices rather than in one piece reviewed as a whole. That is
acceptable while each slice implements behaviour already written down in a design document, and
the logic most at risk is covered by tests.

A slice can span more than one service. In the core sprint, a feature verifying the token touches
the core, which serves the keys, and `canary`, which checks them, in one piece of work.

### When this would be the wrong call

Layers would win if the data model were genuinely uncertain and had to settle before anything
could stand on it, or if several people were dividing the work by tier. The skeleton can be dropped
when the stack is already proven end to end, such as a later sprint on the same pieces, and slices
alone are enough.

---

## What gets tests

**Tests exist to make the thing good, not to prove a sprint is done.** A sprint's checks answer
whether it is finished, once, on the real stack. Tests answer whether something is still true after
a change months later, and whether it holds in cases nobody can produce by clicking. They are chosen
per ticket, for the sake of the code, and never written to mirror a check.

**Anything meeting one of three criteria gets tests:**

- **It is security and it breaks silently.** A missing cookie flag or an open redirect looks
  exactly like a working platform.
- **It cannot be reproduced by hand.** Races, time boundaries, concurrency, a dependency slow in
  one particular way.
- **It is a contract between two languages.** Drift there is otherwise only caught at runtime, and
  a test is the cheapest runtime there is.

Anything meeting none of them is checked by hand when it is built, and that is enough.

**A ticket whose work meets a criterion names its tests in its own definition of done.** Without
that floor, the tests that matter most are the ones skipped, because they are also the awkward
ones, a clock to inject, a database to run, a stub to stand up, and the feature already works when
it is clicked.

**Time is an input.** Anything deciding on expiry, a window or a limit takes the current time as a
parameter rather than reading the clock itself. Otherwise the only way to test it is to wait.

**A guarantee that lives in SQL is tested against a real Postgres, never a mock.** A mocked
conditional update tests the mock. Continuous integration runs a database for this reason.

**A deterministic test beats a race.** Where correctness rests on a single statement, the test
exercises that statement's guarantee directly, for example running the same conditional update
twice and expecting the second to change nothing. Firing concurrent requests and hoping for the
bad interleaving passes whether or not the code is right.

**A feature's authentication tests are part of the feature template.** Verifying the core's token
is the one piece of security logic every feature carries, and the ways it gets broken are known:
an expired token, a bad signature, an unknown key id, and a token naming an algorithm it should not,
`none`, or a symmetric one keyed with the public key. `canary` carries these, along with a test
verifying a token the core actually produced, and every feature after it starts from them.

**No automated browser tests yet.** They would need a new dependency and the proxy's local
certificate authority trusted inside continuous integration, to automate what is walked by hand
once per slice. Worth revisiting when walking by hand becomes frequent, or when regressions start
appearing only in the browser.
