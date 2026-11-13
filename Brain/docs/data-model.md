# The brain's data model

Status: decided and built. Every table below exists; `profile`, `rule`, `topic_level`,
`topic_focus` and `history`, as misconceptions so far, are written, and `state` and `decision`
arrive with the slices that write them.

Last updated: 2026 11 13

The tables the brain stores its memory in, in their final shape. The reasoning behind the shape is
in the three documents beside this one and is not repeated here: `event-model.md` for events,
verbs, topics and relations, `trust-model.md` for stores, status and what is never destroyed, and
`knowledge-model.md` for what the brain is trying to know. This document is the shape itself, so
every slice builds from one description.

Storage is Postgres. How isolation between users is enforced, and why, is in "Multi user, and how
isolation is enforced" in `High-level/docs/platform-architecture.md`.

---

## What holds for every table

- **Scoped to a user.** Every table has `user_id uuid NOT NULL`, the account id the core minted,
  taken from the verified token. The exceptions are named global below.
- **Row level security, enabled and forced**, with one policy for all commands:
  `user_id = nullif(current_setting('brain.user_id', true), '')::uuid`. Forced, so owning the
  table is not a way around it. A query with no user set returns nothing: the setting is missing on
  a connection that never had it and reverts to `''` after a transaction that set it, and a bare
  `current_setting('brain.user_id')::uuid` raises in both cases, where this is null and matches no
  row. `brain.db.CURRENT_USER_SQL` holds the expression, see "The door" in `Brain/README.md`.
  It is the only permissive policy, with no `WITH CHECK` of its own: Postgres ORs permissive
  policies, so a second one, for any command, would let a user past it. A table that needs less
  adds a restrictive policy, which is ANDed with it and only narrows.
- **One door.** The service reaches the database only through a function that opens a transaction
  and sets `brain.user_id` for that transaction alone. Nothing else may take a connection.
- **Two roles.** `brain_owner` owns the tables and runs migrations. `brain_app` is what the running
  service connects as. It may select and insert, update only where a table says so, and never
  delete.
- **Ids are UUIDv7, minted by the brain**, and times are passed in. No column has a default for
  either, so nothing gets an id or a time the code did not choose.
- **Nothing is deleted by the service.** An item becomes inactive, a relation ends, and both stay
  readable. The one exception is the purge command, see `trust-model.md`.

## The tables

```
topics            id, user_id, slug (lowercase letters, digits, hyphens), name,
                  state ('canonical' | 'provisional'), alias_of (topics.id or null),
                  created_at.  unique (user_id, slug).
                  An alias never points at another alias.

topic_relations   id, user_id, from_topic, to_topic, kind (text: 'broader_than',
                  'related_to', open to more), created_at, ended_at (null while it holds).

entities          id, user_id, name, label (free text or null), created_at.
                  unique (user_id, lower(name)).

verbs             GLOBAL, read only to the service. verb (pk), kind ('exposure' |
                  'engagement' | 'judgment' | 'assertion' | 'decision' | 'activity'),
                  strength (1 weak, 2 medium, 3 strong), polarity (-1, 0, 1).

events            id, user_id, verb, subject_topic, subject_entity, subject_item
                  (each null or one id), origin (a feature's name, or 'brain'),
                  source (text or null), run (text or null), occurred_at,
                  occurred_precision ('instant' | 'day' | 'month' | 'year'),
                  recorded_at, evidence (text, not empty), payload (jsonb),
                  payload_version (int), idempotency_key.
                  unique (user_id, origin, idempotency_key).
                  Append only: the service may insert and read, never update.

items             id, user_id, lineage_id (shared by every version of one entry),
                  kind ('profile' | 'rule' | 'topic_level' | 'topic_focus' | 'state' |
                  'history' | 'decision'), store ('profile' | 'procedural' | 'knowledge' |
                  'semantic' | 'reported_history'), nature ('claim' | 'record'),
                  provenance ('asserted' | 'derived'), content (text), value (text or
                  null: a level or a focus), category (text or null: a profile category),
                  details (jsonb), occurred_at, occurred_precision (both null except for
                  history and decisions), status ('active' | 'inactive'),
                  inactive_reason (null | 'superseded' | 'retired'), created_at,
                  created_by_event (events.id).
                  The service may update status and inactive_reason only.

item_subjects     item_id, user_id, topic_id, entity_id (exactly one of the two).
                  An item with no rows here is global.

item_relations    id, user_id, from_item, to_item, kind (text: 'supersedes',
                  'contradicts', open to more), created_at, ended_at (null while it
                  holds), created_by_event.

item_evidence     id, user_id, item_id, event_id, kind ('supports' | 'contradicts'),
                  created_at.

schema_migrations GLOBAL, the owner's only. version, name, applied_at.
```

`verbs` and `schema_migrations` are the two global tables: they hold nothing about a person, so
they have no `user_id` and no policy.

## Which kind goes in which store

The store decides who may change a claim, see "Authority follows the store, not the source" in
`trust-model.md`.

| Kind          | Store              | Nature | What it is                                                    |
| ------------- | ------------------ | ------ | ------------------------------------------------------------- |
| `profile`     | `profile`          | claim  | Identity, goals, values, interests, preferences, working style |
| `topic_focus` | `profile`          | claim  | What the owner is doing about a topic                          |
| `rule`        | `procedural`       | claim  | How the platform should work with the owner. Always global     |
| `topic_level` | `knowledge`        | claim  | How well the owner knows a topic. Evidence may move it later   |
| `state`       | `semantic`         | claim  | What is going on now. It goes stale                            |
| `history`     | `reported_history` | record | Something that happened, with when                             |
| `decision`    | `semantic`         | claim  | The standing claim that a decision holds                       |

A decision is two things, as "Decisions" in `event-model.md` says. The `decided` event is the
permanent record that it was made, and the item is the claim that it is in force, which a later
decision can supersede.

A profile entry's `category` is one of `identity`, `goals`, `values`, `interests`, `preferences`,
`working_style`.

## Versions

An edit never changes an item. It inserts a new item with the same `lineage_id`, marks the old one
inactive with reason `superseded`, and links them with a `supersedes` relation. An entry's history
is every row of its lineage.

## The verb list

A verb declares where it sits, or it cannot be weighed, see "Verbs" in `event-model.md`. A new verb
is a migration that adds its row.

| Verb        | Kind       | Strength | Polarity |
| ----------- | ---------- | -------- | -------- |
| `exposed`   | exposure   | 1        | 0        |
| `opened`    | engagement | 1        | 1        |
| `completed` | engagement | 2        | 1        |
| `saved`     | engagement | 2        | 1        |
| `abandoned` | engagement | 2        | -1       |
| `skipped`   | engagement | 1        | -1       |
| `rated`     | judgment   | 3        | 0        |
| `accepted`  | judgment   | 3        | 1        |
| `rejected`  | judgment   | 3        | -1       |
| `edited`    | judgment   | 3        | -1       |
| `retired`   | judgment   | 3        | -1       |
| `restored`  | judgment   | 3        | 1        |
| `confirmed` | judgment   | 3        | 1        |
| `asserted`  | assertion  | 3        | 1        |
| `decided`   | decision   | 3        | 1        |
| `worked`    | activity   | 1        | 0        |

`edited`, `retired`, `restored` and `confirmed` are the brain's own: they record what the owner did
to an item, with `subject_item` naming it.

## What is computed, never stored

Confidence and currency are not columns, as "Confidence, currency and status are three different
things" in `trust-model.md` requires. What a read returns is the facts they would be computed from:
how many events support and contradict an item, of which kinds, and when the newest support
happened. No score is derived from those yet. The weights wait until the evaluation cases can test
them.
