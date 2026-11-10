# How we build

Status: exploratory. The walking skeleton is being built.

Last updated: 2026 11 10

How work gets cut up and ordered, as opposed to what gets built. What a particular feature must
deliver, and the slices that get it there, live on the GitHub Project rather than here, see
`docs/README.md`. Who does each part, the owner planning and agents implementing, reviewing and
merging, is in `agent-workflow.md`.

Everything below was decided while planning the core sprint and argued against that sprint's
checks. It is written as platform wide because nothing in it is specific to the core, but it has
not yet met the brain. Planning the brain track is where it gets re examined, and if the brain
needs something different, that goes here as a difference with its reason rather than as a
second copy in `Brain/docs/`.

---

## A walking skeleton, then vertical slices

**Work is cut vertically.** A slice goes through every tier it touches, storage, logic, HTTP and
the page, and ends in something that can be used and tested end to end. It is not cut by layer,
building the schema, then the logic, then the endpoints, then the pages.

**When the pieces have never been connected, the first piece of work is a walking skeleton**: the
thinnest path through every moving part, end to end, with no behaviour in it. The proxy on the real
hostname with real HTTPS, each service reachable through it, the database with its roles and the
migrations command working. It holds no authentication and no domain code.

**Slices are ordered by risk, not by the order a feature's behaviours happen to be listed in.**
The claim most likely to be wrong gets tested first, while little is built on top of it. Within
that, the slices the owner can try soonest come first.

**What a feature must do is written as acceptance criteria on its slices, not as tickets.** Most
criteria are a behaviour one slice delivers. Some are properties that fall out of the design, such
as staying logged in across a restart, and those get attached to the slice that makes them true
rather than becoming work of their own.

**Acceptance criteria are automated tests, written first.** Each criterion is a named test, a cargo
test, a pytest test or a Playwright test in `e2e/`, that the agent writes before the code and
watches fail. A slice is done when they pass. Because they are tests, they keep running: the
Playwright suite runs in continuous integration on every pull request that touches service code, so
a later slice breaking an earlier one fails its own pull request instead of waiting to be noticed.
At the end of a feature, the full suite runs once more on a fresh start of the whole stack, as the
feature being done. A criterion that cannot be written as a test is a sign the slice is not
understood yet, and it is not Ready until it can be.

**On the board, work is a feature, then slices, then a task checklist.** A feature is a parent
issue. Each slice is a sub-issue of it, one branch and one pull request, sized so an agent finishes
it in one run and a reviewer judges it in one read. Its tasks are a checklist in the slice, one
commit each, not issues of their own. Only the slice has to end in something testable.

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

The skeleton is the one piece of work that no behaviour stands behind, so its done condition has to
be written as concretely as an acceptance test would be, or it has no finish line.

Security logic lands across several slices rather than in one piece reviewed as a whole. That is
acceptable while each slice implements behaviour already written down in a design document, and
the logic most at risk is covered by tests.

A slice can span more than one service. In the core sprint, a feature verifying the token touches
the core, which serves the keys, and `canary`, which checks them, in one piece of work.

### When this would be the wrong call

Layers would win if the data model were genuinely uncertain and had to settle before anything
could stand on it, or if several people were dividing the work by tier. The skeleton can be dropped
when the stack is already proven end to end, such as a later feature on the same pieces, and slices
alone are enough.

---

## What gets tests

**Acceptance tests prove a slice does what was planned. The rest exist to make the thing good.**
The acceptance tests answer whether a slice is finished, and keep answering it on every later pull
request. The tests below answer whether something holds in cases nobody can produce by clicking,
and whether it is still true after a change months later. They are chosen per slice, for the sake
of the code, and are more than the acceptance tests whenever the code meets a criterion below.

**Anything meeting one of three criteria gets tests:**

- **It is security and it breaks silently.** A missing cookie flag or an open redirect looks
  exactly like a working platform.
- **It cannot be reproduced by hand.** Races, time boundaries, concurrency, a dependency slow in
  one particular way.
- **It is a contract between two languages.** Drift there is otherwise only caught at runtime, and
  a test is the cheapest runtime there is.

Anything meeting none of them is covered by its slice's acceptance tests, and that is enough.

**A slice whose work meets a criterion names those tests in its Acceptance.** Without
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

**Browser tests drive the real stack.** The suite in `e2e/` is Playwright, run through Caddy on
`https://platform.test` the way a person would use the platform, in continuous integration as well
as on the Mac mini. The browser ignores TLS errors rather than trusting Caddy's local certificate
authority, since TLS is covered by `curl --cacert`; how to run it and its fixtures are in
`e2e/README.md`.
