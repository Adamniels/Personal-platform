# brain

The platform's memory, a service of its own that every feature depends on and that is not itself a
feature. Why it is a separate service is in "The brain is its own service" in
`High-level/docs/platform-architecture.md`; what it will hold is in `docs/`, starting at
`docs/brain-architecture.md`, with the tables in `docs/data-model.md`.

Today it serves a health endpoint, says who is asking, holds each user's subjects, the topics and
named things their memory will be about, records the events features send it and shows them as a
timeline, holds the memory items the owner enters by hand, profile entries, rules, a level and a
focus on each topic, and misconceptions, each with the event that proves it was said, lists every
topic with what he knows of it, and serves the memory centre, its own application, whose sections are
still empty. Its database has the roles, the migrations, the one door every query goes through,
proven to keep two users apart, the subjects' three tables, the verbs and the events, and the
memory store's four tables, every table in `docs/data-model.md`. One account's memory can be
exported to a file and imported into an empty brain, from a terminal.

## What it serves

- `GET /brain/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what the brain depends on.
- `GET /brain/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.
- **Subjects**, what a user's memory is about, see "Scope is topic and entity, never feature" in
  `docs/trust-model.md`. Each route needs a valid token, acts for its account alone, and answers
  `{"detail": ...}` when it refuses: `404` for an id the user does not have, another user's
  included, `409` for a second of something unique, and `422` for a body that does not parse or a
  request that breaks a rule. The rules live in `src/brain/topics.py` and `src/brain/entities.py`,
  the routes in `src/brain/subjects.py`.
  - `GET /brain/api/topics`, every topic the user has, aliases included, by slug: `id`, `slug`,
    `name`, `state` (`canonical` or `provisional`), `alias_of`, `resolves_to`, the topic it is read
    as, and `created_at`.
  - `POST /brain/api/topics`, `{"name": ..., "slug": ...}`, a new canonical topic, `201`. A slug is
    lowercase letters, digits and hyphens, starting with a letter or digit, 1 to 80 characters;
    without one it is made from the name, lowercased with every other run of characters one hyphen.
    One the user already has is `409`. A provisional topic is made only by an event.
  - `POST /brain/api/topics/{id}/promote` makes a provisional topic canonical.
  - `POST /brain/api/topics/{id}/same-as`, `{"target_id": ...}`, makes the topic an alias of a
    canonical one. Only `alias_of` changes: whatever names the alias keeps naming it and is read
    as the target, so `separate` loses nothing. Refused when the target is itself, an alias or
    provisional, when the topic is an alias or has aliases, or when one is under the other.
  - `POST /brain/api/topics/{id}/separate` makes an alias its own topic again.
  - `POST /brain/api/topics/{id}/broader`, `{"broader_id": ...}`, places the topic under a broader
    one, `201`, a `broader_than` relation from the broader topic to this one. Refused when it would
    put a topic under itself, directly, through others or through an alias.
  - `POST /brain/api/topics/{id}/related`, `{"related_id": ...}`, relates the topic to another,
    `201`, a `related_to` relation from this topic to the other, read either way round and through
    aliases. Refused for the topic itself, read through aliases, and `409` when it already holds
    either way round. Asking about one lists the other by slug, and nothing more.
  - `GET /brain/api/topic-relations`, every relation between the user's topics that holds, of
    every kind, in the order they were made: `id`, `from_topic`, `to_topic`, `kind`, `created_at`
    and `ended_at`, null. Each end is the topic as it was given, an alias included, for the caller
    to read through `resolves_to` from `GET /brain/api/topics`. An ended relation is not listed.
  - `POST /brain/api/topic-relations/{id}/end` ends a relation. The row stays, with `ended_at`.
  - `GET /brain/api/entities` and `POST /brain/api/entities`, `{"name": ..., "label": ...}`, the
    user's named things, such as a project: a name compared without letter case, so a second one
    in any case is `409`, and an optional free text label. Only a name, never a link into a
    feature's data.

  Every change to a user's topics or their relations takes that user's lock for its transaction,
  `topics.lock_vocabulary`, since the checks above read rows another change could be writing.
  `topics.resolve` and `topics.narrower`, every topic under one through relations that hold,
  aliases resolved, are what retrieval reads them with.
- **Events**, what happened, in the shape of "The shape" in `docs/event-model.md`: the endpoint
  every feature sends what it observed to, "record event" in "The two contracts" in
  `High-level/docs/feature-contract.md`. The rules live in `src/brain/events.py`, the routes in
  `src/brain/event_routes.py`, and they answer and refuse as the subjects' routes do.
  - `POST /brain/api/events`, `201` with the event. The body carries `verb`, one of the verbs
    seeded from "The verb list" in `docs/data-model.md`; at most one of `topic`, a slug, and
    `entity`, a name; optionally `item`, the id of one of the user's memory items, for
    what the owner did to it, `422` when they have none by it; `origin`, the sending feature's name, by the slug's rule, never `brain`, which
    is the brain's own; optional `source` and `run`; `occurred_at`, with a zone, and
    `occurred_precision`, `instant` (the default), `day`, `month` or `year`, both standing for
    the time it was recorded when left out; `evidence`, readable text, not blank, at most 4000
    characters; `payload`, a JSON object of at most 16 KiB as compact JSON, opaque to the brain;
    `payload_version`, 1 or more; and `idempotency_key`. A field it does not know is `422`, so a
    misspelt one is not lost. `recorded_at` is the brain's own time.
  - Sent again with the same `origin` and `idempotency_key`, it is `200` with the event already
    recorded, unchanged, whatever else the second one carries. The key is the user's: another
    origin's, or another user's, is another event.
  - A slug the user has no topic for makes a `provisional` topic, named by its slug, and a name
    they have no named thing for makes one, with no label, both silently. An event names a topic
    as it was given, an alias included, and is read through it: its `subject_topic` is the one it
    named, and `topic` is the one that resolves to now, so marking topics the same or separating
    them rewrites no event.
  - `GET /brain/api/events`, the timeline: the user's events, newest recorded first, `limit` of
    them (50 by default, 1 to 200), and with `before`, a time with a zone, only those recorded
    before it. The next page is `before` the last one's `recorded_at`.

  An event is never changed or removed: `brain_app` may insert and read `events`, never update it,
  and may only read `verbs`, which is global. A new verb is a migration that adds its row, with its
  kind, strength and polarity.
- **Memory items**, what the brain holds about the user, in the stores of "Which kind goes in which
  store" in `docs/data-model.md`. The rules live in `src/brain/items.py`, the routes in
  `src/brain/item_routes.py`, and they answer and refuse as the subjects' routes do.
  - `POST /brain/api/items`, an entry made by hand, `201` with the item as a single read answers
    it. The body is told apart by `kind`: `profile`, with a `category`, one of `identity`, `goals`,
    `values`, `interests`, `preferences` and `working_style`, and optional `topics`, slugs, and
    `entities`, names, at most 20 of each; `rule`, which is global, so it takes no subject and
    no category; or `state`, what is going on now, a `claim` in `semantic` with optional `topics`
    and `entities` like a profile entry. Each carries `content`, 1 to 4000 characters once its
    surrounding whitespace is dropped, and an optional `occurred_at`, with a zone, and
    `occurred_precision`, for when it became true, which is when its event happened. A topic the user does not have is `422`, since a hand made entry names one that
    exists; a named thing they do not have is created, with no label. A field it does not know is
    `422`.
  - A level or a focus on a topic, as "How level is represented" in `docs/knowledge-model.md` has
    it: `{"kind": "topic_level", "topic": <slug>, "value": ...}`, `value` one of `shallow`, knows
    the vocabulary, `working`, can use it with help, `solid`, can use it unaided and explain it, and
    `deep`, knows the internals and could teach it, in the `knowledge` store; or
    `{"kind": "topic_focus", ...}`, `value` one of `learning`, `steady` and `not_pursuing`, in
    `profile`. Exactly one `topic`, which the user has, read through its alias: the entry names the
    alias's target. Its `content` is generated, `Level for <topic name>: <value>` or `Focus for ...`,
    and never given. A topic has at most one active entry of each: where it has one, the request is
    an edit of it, `201` with the new version, so `versions` lists the values it has had, each with
    when it was set and its event; the value it has already is `422`, an edit that changes nothing.
    A lock per user, topic and kind, taken before the check, keeps two requests at once from both
    writing a first entry. No migration says one active per topic, since the topic is a row of
    `item_subjects`.
  - A misconception, a history entry on its topic and not a kind of its own:
    `{"kind": "history", "topic": <slug>, "details": {"misconception": true, "believed": ..., "correct": ...}}`,
    each half 1 to 1800 characters once its surrounding whitespace is dropped, with an optional
    `occurred_at` and `occurred_precision`. It is a `record` in `reported_history`, its content
    `Misconception about <topic name>`, then `Believed: ...` and `Correct: ...` on lines of their
    own, and its `occurred_at` the one given or when it was entered.
  - Any other history entry, something that happened, as "Claim versus record" in
    `docs/knowledge-model.md` has it: `{"kind": "history", "content": ..., "occurred_at": ...,
    "occurred_precision": ...}`, both times required, with optional `topics` and `entities`. It is
    a `record` in `reported_history`, its `occurred_at` and precision on the item and its event,
    kept apart from `created_at` and the event's `recorded_at`, when it was entered. A history body
    is a misconception when it carries `details`, and a plain entry when it does not.
  - A decision, as "Decisions" in `docs/event-model.md` has it:
    `{"kind": "decision", "content": ..., "details": {"reasoning": ..., "rejected": [...]}}`,
    `reasoning` and each of at most 20 `rejected` 1 to 4000 characters, `rejected` empty when left
    out, with optional `topics` and `entities` and an optional `occurred_at` and
    `occurred_precision`, when it was made, the request's time when left out. It is a `claim` in
    `semantic`, the claim that it holds, and is written with a `decided` event in place of an
    `asserted` one, the permanent record that it was made.
  - The entry applies at once, as "Standing rules are created deliberately" in
    `docs/trust-model.md` says: one transaction records an `asserted` event, `decided` for a
    decision, through the same path as the events endpoint, with origin `brain`, source `manual-entry`, evidence
    `Entered by hand as <kind>: "<content>"` cut to 500 characters and a payload naming the item,
    then the item, `active`, `asserted` and the first of its lineage, with `created_by_event`
    naming the event, then its subjects, then a `supports` row in `item_evidence`. A failure at any
    step leaves none of it.
  - `GET /brain/api/items`, the user's items, newest first, each with its `topics`, read through
    their aliases, and its `entities`, narrowed by any of `kind`, `status` (`active` or
    `inactive`), `topic`, a slug, and `entity`, a name. A topic matches through aliases on either
    side, and only itself, not the topics under it; a name matches without letter case.
  - `GET /brain/api/items/{id}`, one item with its subjects, its `evidence`, oldest first, each with
    its kind and the event's id, verb, origin, source, evidence and times, and its `relations`, on
    either end. What confidence and currency would be computed from, and neither is stored.
  - `POST /brain/api/items/{id}/edit`, a correction, `201` with the new version as a single read
    answers it. The body names the item's own `kind` and the fields that kind takes, each left out
    to keep what the item has: for `profile`, `content`, `category`, and `topics` and `entities`,
    which replace that kind of subject whole when given; for `rule`, `content` alone. One
    transaction, as "Versions" in `docs/data-model.md` says, inserts the new version with the same
    `lineage_id`, `active` and `asserted` whatever the old one was, holding the old version's
    everything else, its subjects included; sets the old one `inactive` with `superseded`; records
    an `edited` event against it, evidence `Corrected by hand from "<old>" to "<new>"` with each
    text cut to 200 characters, linked to it as `contradicts` evidence; records an `asserted` event
    that makes the new version and supports it; and links the new to the old with `supersedes`,
    made by the `edited` event. `422` for another kind, a field the kind does not take, a topic the
    user does not have, or an edit that changes nothing; `409` for a version that is not its
    entry's newest, or a retired item, which is restored before it is edited.
  - `POST /brain/api/items/{id}/retire` and `POST /brain/api/items/{id}/restore`, putting an item
    away and bringing it back, `200` with it as a single read answers it. Neither takes a body.
    Retiring sets it `inactive` with `retired`, and records a `retired` event against it, evidence
    `Retired by hand: "<content>"`, linked to it as `contradicts` evidence; restoring sets it
    `active` again and records a `restored` event, `Restored by hand: "<content>"`, linked as
    `supports`. Each evidence is cut to 500 characters, and each change is one transaction. `409`
    to retire an item already retired or a superseded version, whose newest version is the one to
    retire, and to restore anything not retired: only a retired item comes back, and a level or a
    focus only while its topic has no other active one. Retiring is the
    owner's action alone, as "Nothing is destroyed, and only the owner retires" in
    `docs/trust-model.md` says: nothing in the brain retires an item for its age or for a score.
  - `POST /brain/api/items/{id}/confirm`, saying a claim still holds, `200` with it as a single
    read answers it. It takes no body and changes nothing on the item: it records a `confirmed`
    event against it, evidence `Confirmed by hand: "<content>"` cut to 500 characters, linked as
    `supports`, which moves when it was last supported. Allowed on any active claim; `422` for a
    record, which asserts nothing about now, and `409` for an item that is not active. It locks
    the item's row before it looks, so a retire at the same time either waits for it or is waited
    for, and support never lands on an item retired meanwhile.
  - `POST /brain/api/items/{id}/replace`, `{"replaced_by": <id>}`, a decision replaced by a newer
    one, `200` with the old one as a single read answers it. Both must be active decisions of
    different lineages: `422` for a `replaced_by` the user does not have, either not being a
    decision, or two versions of one entry, a decision itself included; `409` when either is not
    active. One transaction sets the old one `inactive` with `superseded`, records an `edited`
    event against it, evidence `Replaced by hand: "<old>" by "<new>"` with each text cut to 200
    characters, linked as `contradicts`, and links the new one to it with `supersedes`, made by
    that event, as an edit does. The old one's `decided` event is untouched, and both stay
    readable. Both rows are locked in id order first, so two decisions replacing each other at
    once replace once.
  - `POST /brain/api/items/{id}/contradicts`, `{"contradicts": <id>}`, marks two claims as
    contradicting each other, `201` with the relation, a `contradicts` from this item to the other.
    Both must be active claims: `404` for an id the user does not have, `422` for a `contradicts`
    they do not have, the item itself, or either being a record, and `409` when either is not
    active or the two are already marked, either way round. One transaction records a
    `contradicted` event about this item, evidence `Marked by hand as contradicting: "<one>" and
    "<other>"`, each cut to 200 characters, linked as evidence to neither, and the relation, made by
    it. Neither item changes: what an answer does with the pair is under "Asking". It is the only
    way a contradiction is made, until consolidation exists.
  - `POST /brain/api/item-relations/{id}/end` takes a contradiction back, `200` with the relation,
    ended: the row stays, with `ended_at`, and one already ended is answered as it is. `404` for an
    id the user does not have, `422` for any other kind, since a `supersedes` relation is an
    entry's history and is not taken back.
  - `GET /brain/api/items/{id}/versions`, every version of the entry the item is one of, every row
    of its lineage, oldest first, each as the list answers it, whichever version is asked for. An
    entry never edited has one. `404` for an id the user does not have.
  - `GET /brain/api/knowledge`, what the user knows: one row per topic that is not an alias, by
    slug, each alias folded into its target, with `id`, `slug`, `name`, `state`, `level` and
    `focus`, each the active entry's `id`, `value` and `set_at`, when it was last set, or `null`,
    and `history_count`, its active history entries. A topic with neither is listed too. Narrowed
    by any of `level`, `focus` and `state` (`canonical` or `provisional`); a value off their lists
    is `422`. The query is in `src/brain/knowledge.py`, which owns no rows.
  - `GET /brain/api/now`, what is going on now: the user's active `state` entries, each as the
    list answers it, with `last_supported_at`, the newest `occurred_at` among the events supporting
    it, and `stale`, true when that is more than `STALE_AFTER`, 60 days, before the request; newest
    support first. Both are computed in the query from `item_evidence`, as "Confidence, currency
    and status are three different things" in `docs/trust-model.md` requires, and neither is
    stored. Only a claim of kind `state` is ever stale: a record has no currency, so it has no
    flag. One crude threshold, with no decay curve.
  - `GET /brain/api/history`, what has happened: the user's `history` entries and decisions by
    `occurred_at`, oldest first, each as the list answers it with `replaced_by`. Active ones, and
    each decision another one replaced, `inactive`, with `replaced_by` naming the one that did;
    retired entries and older versions of an edited entry are left out. Narrowed by `topic` and
    `entity` as the list is. Both views are in `src/brain/now_and_history.py`, which owns no rows
    and takes the time as a parameter rather than reading the clock.

  An item is never changed in place: `brain_app` may insert and read the four tables, and update
  only an item's `status` and `inactive_reason`, which editing, retiring, restoring and replacing
  change with one conditional statement each, so of two requests changing the same item at once the second
  finds it changed and is `409`. Which kind lives in which store, and its nature,
  is checked by the migration as well as the code, since the store decides who may change a claim.
- **Asking**, the four questions the brain answers, each with what it knows and why, as "Retrieval:
  per feature calls over shared primitives" in `docs/brain-architecture.md` has it. The routes are
  in `src/brain/ask_routes.py` and call the four building blocks in `src/brain/retrieval.py`,
  `profile()`, `knowledge(topic_ids)`, `history(subject)` and `now()`, which every later per
  feature call reads through too, with `narrower()` and `related()` for the topics around one, and
  `conflicts()` and `ranked()` for the items the owner marked as contradicting. Each block takes
  the door's connection, never calls a model and never leaves the database. Each route is a `POST`, since a question has a body,
  though none changes anything, so the origin check applies to it as to any `POST`. The body
  carries `mode`, `now` (the default) or `history`, and nothing that names a user: the user is the
  token's, and a field a question does not know is `422`.
  - `mode` `now`: active items only, newest `last_supported_at` first, a `state` flagged `stale`
    by the now view's rule. `mode` `history`: age is ignored, so nothing is `stale`, and the order
    is when each thing happened, oldest first: an item's `occurred_at`, or for a kind without one
    the `occurred_at` of the event that made it. A retired item is never in an answer, nor an older
    version of an edited one.
  - Every item in every answer has one shape: `id`, `kind`, `store`, `nature`, `provenance`,
    `status`, `inactive_reason`, `content`, `value`, `category`, `details`, `occurred_at`,
    `occurred_precision`, `subjects`, its `topics` and `entities`, `last_supported_at`, `stale`,
    `support`, the events for it and against it, `supports` and `contradicts`, each counted per
    verb kind with every kind present, and `evidence`, every event behind it, oldest recorded
    first, each with its `verb`, `kind` (`supports` or `contradicts`), `text`, `origin`,
    `occurred_at`, `occurred_precision` and `recorded_at`. There is no relevance score, no
    confidence number and no weight: `support` and `last_supported_at` are what one would be
    computed from, once the evaluation cases can test it.
  - Every answer has `conflicts`: each pair of its items, both active, that the owner marked as
    contradicting and has not taken back, oldest marked first, as
    `{"items": [<id>, <id>], "prefer": <id> | null, "reason": ...}`, the one it was marked from
    first. Both items always stay in the answer, and a superseded or retired one is never in a
    conflict, even where an answer about history holds it. The scopes are compared by `compare` in
    `src/brain/scope.py`: a strictly narrower scope is preferred whatever its age, `"narrower
    scope"`; at an equal scope the newer item, `"newer at equal scope"`; and incomparable scopes,
    neither holding the other, prefer neither, `"incomparable scopes"`, `prefer` `null`. Asked
    about now, each list in the answer puts a preferred item just before the first one it is
    preferred over, and otherwise keeps its order; asked about history, the order is when each
    thing happened alone. A pair is a conflict only where both are in the same answer.
  - `POST /brain/api/ask/profile`, who is this person: `categories`, every one of the six profile
    categories in order, each with its active profile entries, an empty one included, and then
    `rules`. A topic's focus is in the topics answer instead.
  - `POST /brain/api/ask/topics`, `{"topics": [<slug>, ...]}`, 1 to 50, what they know about these
    topics: one entry per slug, in the order asked, with `asked`, the slug as given, `topic`, the
    one that answered, an alias's target, `known`, and its active `level` and `focus`, or `null`,
    its `misconceptions` and its other `history` entries, items about its aliases included. Then
    `narrower`, every topic under it by the `broader_than` relations that hold, to any depth, read
    through aliases, nearest first and then by slug: each once, with the same four fields and
    `via`, the slugs that led to it, from the topic asked down to the one it is directly under, the
    shortest such chain. And `related`, the slugs of the topics `related_to` it, without what is
    known about them. A slug the user has no topic by is `known` `false` with nothing in it, and is
    never created.
  - `POST /brain/api/ask/history`, `{"topic": <slug>}` or `{"entity": <name>}`, exactly one, the
    history of a subject: its `history` entries and decisions, a topic read through aliases, a name
    without letter case, with `asked`, `known`, the `topic` or `entity` that answered, and `items`.
    Asked about history, the decisions another one replaced are there too, `inactive` and
    `superseded`. For a topic, `narrower` holds each topic under it, as the topics answer finds
    them, with its `via` and its own `items`, and `related` the slugs of the topics related to it;
    both are empty for a named thing. An unknown subject is `known` `false` with no items, and is
    never created.
  - `POST /brain/api/ask/now`, what is going on now: the active `state` entries as `items`.
- **Searching**, by words, "search, meaning search memory" in "The two contracts" in
  `High-level/docs/feature-contract.md`. The route is in `src/brain/search_routes.py` and calls
  `search()` in `src/brain/retrieval.py`, a fifth building block beside the four, which returns
  items in their shape too. It is Postgres' own text search, in the `english` configuration, over
  the `search` columns of `items` and `events` in "The tables" in `docs/data-model.md`, and never
  search by meaning: no embeddings, no model.
  - `GET /brain/api/search`, with `q`, the words, 1 to 200 characters once surrounding whitespace
    is dropped; `kind`, repeated for more than one, any of them, every kind when left out;
    `include_inactive`, `false` by default, to find retired items and older versions of edited ones
    too; and `limit`, 50 by default, 1 to 200. A field it does not know is `422`.
  - An item is found when every word of `q` is in what it says, its `content`, `value`, `category`
    or a text value of its `details`, a decision's reasoning among them, or in the evidence of an
    event linked to it in `item_evidence`, for it or against it: every word in the item's own text,
    or every word in one event's evidence, as each has its own `search` column. Word forms match, so `indexing`
    finds `indexes`, but a word is never found inside another, so `cat` does not find `category`.
    `q` is read as plain words: every character that is not a letter or a digit is a space and the
    word `or` is left out, both before `websearch_to_tsquery` sees it, so nothing typed is an
    operator and every word stays required. A `q` of operators or of words too common to search on
    finds nothing, `200` with an empty list.
  - The answer is a list of items in the shape every answer gives, with `excerpt`: about 15 to 35
    words from `ts_headline`, of the item's own text where that holds the words, otherwise of the
    evidence of the newest recorded event behind it that does, each matched word in `<mark>`. Every
    other character is escaped for HTML before the excerpt is cut, since a page shows it, so the
    marks are its only markup. Ordered by `last_supported_at`, newest first, then by id; Postgres'
    rank is not used and not returned, since it would be a relevance score by another name, which
    waits for the evaluation cases. `stale` is the now view's rule.
  - Every hand made entry is supported by an event whose evidence quotes it as
    `Entered by hand as <kind>: ...`, so a word such as `hand` or a kind's name finds every such
    entry through its evidence.
- `GET /brain`, `GET /brain/<section>` and a topic's page, `GET /brain/knowledge/<slug>`, the
  memory centre's `index.html` from the build, see "The memory centre" below. Without a valid token, a `303` to the core's
  `/auth/refresh?return_to=<the path>`.
- `GET /brain/assets/<file>`, the build's files, with or without a session, since they hold code
  and never data.

**Who is asking.** Every route under `/brain/api/` except `health` needs a valid access token, and
the brain checks it with `platform-auth`, the library in `libs/python/platform-auth/`, as every
Python service does: the token from the `Authorization` header or the `__Host-access` cookie,
verified against the core's key set, and a request without a valid one answered `401` with an
empty body, never a redirect, since a script cannot follow one. The memory centre's pages are the
exception: they go through the library's `page` wrapper instead, which sends a browser without a
valid token to the core's refresh endpoint with the way back. The user id in the
brain is always the verified token's `Claims.account_id`, never a header, a parameter or a body
field. `authenticated` and `page` in `src/brain/edge.py` put the library's `api` and `page`
wrappers on a FastAPI route, so FastAPI sees a handler that takes only the request. Such a handler
reads its own body and address, with `body_as` and `path_id` beside them, after the token is
checked, so a request without a valid one is `401` whatever its body; `documented` puts what it
reads in the schema. In front of the whole app, routing
included, the library's `OriginCheck` refuses with `403` any request other than GET, HEAD and
OPTIONS whose `Origin` is not exactly `BRAIN_PUBLIC_ORIGIN`.

Every route sits under `/brain`, which the Caddyfile sends here with the prefix kept, so the
brain's routes are written with it: the API under `/brain/api/`, the memory centre beside it.
Everything else is `404`, FastAPI's own `/docs`, `/redoc` and `/openapi.json` included: they would
sit outside the prefix, and through the proxy would never reach the brain anyway. The brain has no
entry in `infra/features.toml`, since it is not a feature and has nothing for the front page; the
nav bar's link to the memory centre is fixed in `design/nav.js`, see "Navigation" in
`High-level/docs/platform-architecture.md`.

The schema FastAPI generates from the routes and their models is committed as
`contracts/brain/openapi.json`, and `tests/test_contract.py` fails when the app no longer generates
it byte for byte. The memory centre's page routes are left out of it, since they are not API. Changing what the brain serves means regenerating it, with
`UPDATE_CONTRACTS=1 uv run pytest tests/test_contract.py`, and reviewing the diff, see
`contracts/README.md`. It needs `BRAIN_APP_DATABASE_URL` set, as every test run does, since
`brain.app` loads the settings when it is imported; nothing connects to it.

## Running it

From this directory, with uv installed and the stack in `infra/` up. Each command that touches the
database takes its connection string from the environment, built from `infra/.env` on the command
line so no password is written to a file:

```zsh
BRAIN_OWNER_DATABASE_URL="postgres://brain_owner:$(grep '^BRAIN_OWNER_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/brain" \
  uv run brain migrate
BRAIN_APP_DATABASE_URL="postgres://brain_app:$(grep '^BRAIN_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/brain" \
  uv run uvicorn brain.app:app --host 127.0.0.1 --port 8082 --reload
BRAIN_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" \
BRAIN_APP_DATABASE_URL="postgres://brain_app:$(grep '^BRAIN_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/brain" \
  uv run pytest
uv run ruff check
uv run ruff format --check
uv run mypy
```

The tests need both strings, and fail rather than skip without them. Each test that touches the
database gets one of its own, copied from a template migrated once per run, and connects as
`brain_app`; `tests/harness.py` says how.

`uv run` builds `.venv` from `uv.lock` on first use. Port 8082 is the brain's everywhere, beside
the core's 8080 and canary's 8081. It stays a uvicorn flag rather than a setting, since uvicorn
binds it before the brain's code runs.

## The memory centre

The brain's own application, `web/`, over its own data, described in "The brain owns its own
surface" in `docs/brain-architecture.md`. It is a frontend like any other, a `web` folder under its
service, so CI and the agents gate and build it with the rest, see `design/ui/README.md`; it
depends on the components there as `"@platform/ui": "file:../../design/ui"`.

The page is an `h1`, "Memory", a line under it, then the package's `Tabs`, one per section, each
with its own address:

| Section    | Address             |
| ---------- | ------------------- |
| Profile    | `/brain`            |
| Rules      | `/brain/rules`      |
| Knowledge  | `/brain/knowledge`  |
| Now        | `/brain/now`        |
| History    | `/brain/history`    |
| Everything | `/brain/everything` |
| Timeline   | `/brain/timeline`   |
| Ask        | `/brain/ask`        |

The address is the state, through the History API and no router library (`src/address.ts`): a tab
pushes its section's address, Back and Forward come back as `popstate`, and an address that names
no section shows a not found state under the tabs. The brain answers `index.html` for any single
segment under `/brain/` other than `api` and `assets`, so the list of sections lives in
`src/sections.ts` alone, and for a slug under `/brain/knowledge/`, a topic's page, the one section
with pages of its own. Anything deeper is `404`. Until its slice lands, a section shows the package's `EmptyState`.

**Profile and Rules** are where the owner enters by hand who he is and how the platform should work
with him, `src/profile/` and `src/rules/` over the shared pieces in `src/entries/`:

- **Profile** is one `Card` per category, in the card grid, in the order identity, goals, values,
  interests, preferences, working style, each listing its active entries, or a line saying it has
  none. "Add" at a card's foot, secondary, opens a text area in place; "Add entry", the section's
  one primary button, asks for the category first.
- **Rules** is one list of the active rules, under the line "How the platform and its agents should
  work with you. A rule applies everywhere, at once.", with "Add rule" as its primary button.
- Both list an entry where its first version was made, oldest first, so a correction keeps its
  place. They fetch every version of their kind, `GET /items?kind=...` with no status, which is also
  what says whether an entry has earlier versions; `src/entries/entries.ts` holds the queries and
  the changes. Each entry ends with Edit and Retire, ghost buttons, and Earlier versions when it has
  more than one version. Edit opens its text in place and saves it with `edit`, a new version; the
  same text again sends nothing. Earlier versions lists the older ones from `versions`, oldest
  first, each with when it was made in the mono font. Retire asks first, in the package's `Dialog`,
  and then a toast "Entry retired" offers Undo, which calls `restore`. Nothing is deleted: a retired
  entry stays in Everything.
- Empty text is refused in the form, and a `422` is shown on the text area. Every save, retire and
  restore refreshes every query under `items`, so each list showing the entry shows it as it now is.
  Entries are entered without subjects here, and a category is not changed: retire the entry and
  add it again.

`index.html` is the same shell as every page with the bar: the design files and `nav.js` from the
proxy, not bundled, and `<platform-nav current="brain">`. Vite builds it with base `/brain/` into
`web/dist`, git ignored, its files under `dist/assets/`.

**Calling the brain.** `src/api/client.ts` is the one way the application calls the API: every call
to `/brain/api/` with the session cookie, a state changing one with a JSON body. On a `401` it posts
once to `/auth/refresh` with a form encoded `return_to`, the current path, and repeats the call;
calls that get a `401` together share that refresh. A second `401` sends the browser to
`/auth/refresh?return_to=<the path>`, and the core decides whether a login is needed. Any other
failure, the network's included, is an `ApiError` carrying a typed `Problem`: the status, a title
and a detail read from a problem document or from FastAPI's `detail`. React Query holds what the
server answered, and every failed query or change raises an error toast, in one place,
`src/providers.tsx`, with the package's `ToastProvider`. A change whose form shows a `422` on its
field says so with `meta: { fieldErrors: true }`, and that refusal raises no toast as well. A GET's
path parameters and query string are typed from the schema too:
`client.get("/items/{id}/versions", { path: { id } })`. `src/providers.tsx` also holds the one
client, which `useClient()` returns; a test passes its own, over a fetch it answers, as
`src/test/fake-brain.ts` answers the item routes.

**The API's types.** `src/api/schema.d.ts` is generated from `contracts/brain/openapi.json` by
`tools/generate-schema.mjs` and committed, and `src/api/schema.test.ts` fails when it is stale. After
the schema changes, regenerate it with `npm run generate:api` and review the diff. `client.get`
takes its paths and answer types from it.

From `web/`, with Node 26:

```zsh
npm ci
npx biome ci .
npx tsc --noEmit
npx vitest run
npm run build
```

The brain serves `web/dist` once it is built; it has no dev server of its own.

## Settings

The brain reads three settings from the process environment, once, in `src/brain/settings.py`. It
loads no `.env` file; `.env.example` lists them for whoever exports them. Unset means the default,
and a value that is set must parse, or the brain refuses to start rather than quietly using the
default. The rules are canary's, written out again rather than imported, since no service imports
from another's directory.

- `BRAIN_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `BRAIN_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.
- `BRAIN_WEB_DIR`, default `web/dist`: where the memory centre's build is, absolute or relative to
  the directory the brain runs from. It must not be empty. Whether it holds a build, an
  `index.html` and an `assets/` folder, is checked when the brain starts, which stops with an error
  naming the setting when it does not; the settings themselves load without one, so the brain's
  tests and its CI job need no build.
- `BRAIN_APP_DATABASE_URL`, required, with no default: the brain's database as `brain_app`, such as
  `postgres://brain_app:<password>@127.0.0.1:5432/brain`. It holds a password, so it is never
  shown, in an error or in the settings' `repr`, and is set on the command that starts the brain
  rather than written to a file.

The defaults are right on the Mac mini and in CI, so neither sets them. The database address
has no default, and every place that starts the brain sets it.

## The database

The brain has a database of its own, `brain`, beside the core's, in the one Postgres in `infra/`,
created with its two roles by `infra/postgres/initdb/brain.sh`, see "The databases" in
`infra/README.md`. Why a database per service, and why the roles are split, is in "Databases,
migrations, and running it" and "Multi user, and how isolation is enforced" in
`High-level/docs/platform-architecture.md`.

- **`brain_owner`** owns the tables, and runs the migrations and the purge. Nothing running
  connects as it.
- **`brain_app`** is what the running brain connects as. It owns nothing, so row level security
  applies to it, and `migrations/0001_default_privileges.sql` grants it `SELECT`, `INSERT` and
  `UPDATE` on every table the owner creates, never `DELETE` or `TRUNCATE`. A table that needs less
  revokes it in its own migration.

### Migrating

Migrations are plain SQL files in `migrations/`, named `NNNN_snake_description.sql`, forward only:
an applied file is never edited, renamed or removed, and a change is a new file. They run only as a
command, `uv run brain migrate`, never at startup, since the app role is not allowed to.

The command reads `BRAIN_OWNER_DATABASE_URL` from the environment and nothing else, takes a session
advisory lock so two runs cannot overlap, and records each file in `schema_migrations (version,
name, applied_at)`. Each new file runs in its own transaction with its ledger row, so a failing file
leaves nothing behind and stops the run before any later file. It prints `applied <version>
<name>` per file, or `nothing to do, every migration is applied`. It refuses, applying nothing,
when the files and the ledger disagree: a file name it cannot order, two files with one version, an
applied file renamed or gone, or a new file numbered below one already applied.

### The door

Every query goes through `brain.db.transaction(pool, user_id)`, an async context manager that takes
a connection from the pool, opens a transaction and sets `brain.user_id` for that transaction
alone, with `set_config(..., true)`, the `SET LOCAL` the isolation section requires. Nothing else
in the package takes a connection from the pool, and a test checks it. The pool, a
`psycopg_pool.AsyncConnectionPool` as `brain_app`, is opened in the app's lifespan and waits for its
first connections, so a database it cannot reach stops the brain at startup.

Every scoped table follows "What holds for every table" in `docs/data-model.md`: `user_id uuid NOT
NULL`, row level security enabled and forced, and one policy,
`USING (user_id = nullif(current_setting('brain.user_id', true), '')::uuid)`, the expression
`brain.db.CURRENT_USER_SQL` holds. With no user set it is null, so a query outside the door
returns nothing rather than everything. `tests/test_isolation.py` proves it on a probe table, and
fails for any table a later migration adds to `public` without that guard, or with any other
permissive policy beside it, except the global ones the data model names.

### Export and import

Two commands write one account's whole memory to a file and read it back into an empty brain, so
what the owner enters is not held in one database on one laptop. Neither is reachable over HTTP.
Both take `BRAIN_APP_DATABASE_URL`, as the running brain does, and work through the door for the one
account, so row level security holds for them like for any request: an export cannot read another
user's rows, and an import cannot write any.

```zsh
BRAIN_APP_DATABASE_URL="postgres://brain_app:$(grep '^BRAIN_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/brain" \
  uv run brain export <account-id> <file>
BRAIN_APP_DATABASE_URL="postgres://brain_app:$(grep '^BRAIN_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/brain" \
  uv run brain import <file> [--as <account-id>]
```

- **`brain export <account-id> <file>`** writes the memory file, the format in "The memory file" in
  `docs/data-model.md`, stamped with the command's clock as `exported_at`. It reads every table in
  one statement, so the file is one snapshot. It never writes over a file that exists, creates the
  file readable by its owner alone, and prints one line saying how many rows it wrote and that the
  file is the whole memory in plain text.
- **`brain import <file> [--as <account-id>]`** reads it back, in one transaction: all of the file
  or none of it. Every id and every time is kept, so nothing is minted again and nothing gets
  today's date. It refuses, changing nothing and exiting non zero with what is wrong, a file of a
  newer format, a malformed one, an event whose verb the brain does not know, and an account that
  already holds anything in any of the tables, since merging a file into a memory is not something
  it does. Two imports for one account at once take that account's lock, so the second finds the
  first's rows and refuses.
- **`--as`** writes every row under another account id and changes nothing else. It is for moving a
  memory to a platform where the account was registered again, and so has a new id. Into the brain
  it came from, the ids are already taken and the import refuses.

**Keep the file somewhere private.** It holds everything the brain knows about the owner, in plain
text, unencrypted: treat it like a password manager's export, never in a repository, a shared
folder or a chat. An export by hand is not a backup plan, see "Deployment and backups are outside
the first milestone" in `High-level/docs/platform-architecture.md`.

### Purging

Retiring is the normal way to put an entry away: it stops applying and stays readable, and it can
be brought back, see "Nothing is destroyed, and only the owner retires" in `docs/trust-model.md`.
Purging is for the rare entry that must truly be gone, such as a password pasted into a field by
mistake, which retiring would leave readable. It is a command run from a terminal, deliberately,
and nothing else: it is not reachable over HTTP, and the memory centre has no button for it.

```zsh
BRAIN_OWNER_DATABASE_URL="postgres://brain_owner:$(grep '^BRAIN_OWNER_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/brain" \
  uv run brain purge <account-id> <item-id>
```

It connects as `brain_owner`, from `BRAIN_OWNER_DATABASE_URL` alone, as `brain migrate` does. The
owning role may delete, which `brain_app` may not, and that is the whole of the bypass; `brain_app`
has no `DELETE` on any table, and `tests/test_purge.py` checks it still has none. Row level security
is forced, so it applies to the owner too: the command sets `brain.user_id` to the account it is
given for its one transaction, as the door does, and sees and removes that account's rows alone. An
item id the account does not have, its own unknown or another account's, gets the same answer and
a non zero exit.

**What goes.** Every version of the entry, the items sharing the given item's `lineage_id`,
whichever version is named; their `item_subjects`, their `item_relations` on either side, and their
`item_evidence`; and every event linked to those rows, by `subject_item`, by `item_evidence`, or as
the event that made an item or a relation, unless something that stays still references it. Topics
and named things never go, since the owner chose them and other entries may use them.

**What stays.** An event another entry rests on, as its evidence, its subject or the event that made
it, stays. Its `subject_item` is cleared when it named a removed item, and its text is left as it
was, so it may still quote the purged entry. The listing shows each such event before anything is
removed. A replaced decision is the common case: purging the newer one keeps the `edited` event
against the older, which quotes both, and leaves the older inactive, superseded, with no relation
naming what replaced it. Purge the older one too when that text must go.

**How it asks.** It prints every row it will remove, one per line, by table and id with the first
80 characters of its text, then the events it keeps, then `Type yes to remove these <n> rows for
good:`, and reads one line. Only the exact word `yes` proceeds; anything else, an empty line or the
end of input included, removes nothing and exits non zero. The listing, the answer and the removal
are one transaction, with the entry's items and linked events locked, so nothing new can come to
rest on them in between, and a failure part way removes nothing.

It writes no event about the purge, since the point is that nothing about the entry remains. What
it prints, `removed <n> rows for good` with the count per table, is the only record.

## Building the app

`create_app(settings, keys=None)` in `src/brain/app.py` builds the app, so tests build one from
settings of their own. When the app starts, it checks the memory centre's build, opens the database
pool and, without `keys`, creates one HTTP client and one key cache over it; when it stops, it
closes what it opened, in reverse. Tests pass a cache of their own, over the committed key set
answered from memory, and verify the committed token, see `contracts/README.md`. That token
expires at a fixed time, so the tests pin the clock the library reads, with the `at` fixture in
`tests/conftest.py`, beside the contract token, a folder shaped like the build and the database
fixtures. A test that builds the app without starting it loads `MINIMAL_ENV` from
`tests/harness.py`, a well formed database address nothing connects to; one that starts it is
given a database of its own.

A test that calls the running brain as more than one user, as the subjects' tests do, cannot use
the contract token, which names one account and whose private key nobody holds. `calling(user)` in
`tests/conftest.py` starts the brain on the test's database over a key set of the tests' own, and
mints each user a token signed with its private half, by the real clock. The signing is `pyjwt`
and `cryptography`, which `platform-auth` already installs.

## Libraries the brain is built on

**uv, a packaged `src/` layout, uvicorn, pytest and ruff**, as in `canary/`, for the reasons its
README gives.

**FastAPI for HTTP.** The brain is the platform's widest API: every feature calls it, with request
bodies to validate, so typed request and response models and a schema generated from them pay for
themselves here, which they did not in canary. FastAPI is Starlette underneath, so middleware
written for canary, such as its `Origin` check, carries over unchanged. Its schema is what the
brain's callers are written against.

**mypy, in strict mode, over `src` and `tests`**, configured in `pyproject.toml`. The brain's
models are its contract, so the types are checked rather than decorative. pyright was the
alternative, faster, but its Python package downloads Node when it runs, which does not suit an
unattended machine.

**platform-auth for who is asking**, a path dependency on `../libs/python/platform-auth`, the
one copy of token verification and the `Origin` check every Python service installs. What it holds
and why it is shared is in its README.

**The memory centre's libraries**, in `web/package.json`, pinned exactly: the same React,
TypeScript, Vite, Vitest, Testing Library, jsdom and Biome as `design/ui`, with the same settings;
`@tanstack/react-query` 5.107.1 for what the server holds, so a change refreshes every view that
shows it; `openapi-typescript` 7.13.0 to generate the API's types; and `@types/node` 26.6.6 for the
one test that runs the generator. `openapi-typescript` builds its output with the TypeScript
compiler's JavaScript API, which TypeScript 7 no longer exports, and declares a peer of
TypeScript 5. So `package.json` installs TypeScript 5.9.3 beside 7 under the name `typescript-5`,
an override tells npm that openapi-typescript's peer is the project's TypeScript, and the
generator, alone, resolves its `typescript` import to the alias. `npx tsc` stays TypeScript 7.
Remove the three once openapi-typescript works with TypeScript 7.

**httpx2 for HTTP calls**, a runtime dependency, since the brain creates the client the key cache
fetches the core's key set with. FastAPI's `TestClient` is Starlette's and uses it too.

**psycopg 3, with its pool, for Postgres**, `psycopg[binary,pool]`. The standard Postgres driver,
with plain SQL and no ORM, so every transaction and the user setting the door sets stay visible in
the code rather than inside a library. It is async, as FastAPI is, and its pool is the one the door
takes connections from. The binary extra ships libpq, so no Postgres client has to be installed.

**pytest-asyncio**, a dev dependency, so tests can be async and use the pool the way the app does.
