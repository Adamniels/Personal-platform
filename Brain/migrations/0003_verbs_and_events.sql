-- What happened: the verb vocabulary, seeded, and the one table every event
-- goes in. The shape is "The tables" and "The verb list" in
-- docs/data-model.md, and why it is one table is "One table for all events"
-- in docs/event-model.md.

-- Global: a verb says nothing about a person, so there is no user_id and no
-- policy, and the data model names it global. The service only reads it. A
-- new verb is a new migration that adds its row, with where it sits.
CREATE TABLE verbs (
    verb text PRIMARY KEY CHECK (verb ~ '^[a-z][a-z_]*$'),
    kind text NOT NULL CHECK (
        kind IN ('exposure', 'engagement', 'judgment', 'assertion', 'decision', 'activity')
    ),
    -- 1 weak, 2 medium, 3 strong, as evidence.
    strength smallint NOT NULL CHECK (strength BETWEEN 1 AND 3),
    polarity smallint NOT NULL CHECK (polarity BETWEEN -1 AND 1)
);

INSERT INTO verbs (verb, kind, strength, polarity) VALUES
    ('exposed', 'exposure', 1, 0),
    ('opened', 'engagement', 1, 1),
    ('completed', 'engagement', 2, 1),
    ('saved', 'engagement', 2, 1),
    ('abandoned', 'engagement', 2, -1),
    ('skipped', 'engagement', 1, -1),
    ('rated', 'judgment', 3, 0),
    ('accepted', 'judgment', 3, 1),
    ('rejected', 'judgment', 3, -1),
    ('edited', 'judgment', 3, -1),
    ('retired', 'judgment', 3, -1),
    ('restored', 'judgment', 3, 1),
    ('confirmed', 'judgment', 3, 1),
    ('asserted', 'assertion', 3, 1),
    ('decided', 'decision', 3, 1),
    ('worked', 'activity', 1, 0);

-- What an event about a named thing points at, carrying user_id, as topics
-- already has, so it can only name the same user's.
ALTER TABLE entities ADD UNIQUE (user_id, id);

CREATE TABLE events (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    verb text NOT NULL REFERENCES verbs (verb),
    -- What it is about: a topic or a named thing, at most one, or neither.
    -- The topic as it was named, an alias included; it is read through the
    -- alias, so nothing here changes when topics are merged or separated.
    subject_topic uuid,
    subject_entity uuid,
    -- A memory item, for what the owner did to it. The items table arrives
    -- with the memory store, which adds the reference beside the two below.
    subject_item uuid,
    -- The feature that sent it, or 'brain' for an entry made in the brain.
    origin text NOT NULL CHECK (origin ~ '^[a-z0-9][a-z0-9-]{0,79}$'),
    source text CHECK (source <> ''),
    run text CHECK (run <> ''),
    occurred_at timestamptz NOT NULL,
    occurred_precision text NOT NULL CHECK (
        occurred_precision IN ('instant', 'day', 'month', 'year')
    ),
    recorded_at timestamptz NOT NULL,
    evidence text NOT NULL CHECK (btrim(evidence, E' \t\n\r') <> ''),
    payload jsonb NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    payload_version integer NOT NULL CHECK (payload_version >= 1),
    idempotency_key text NOT NULL CHECK (idempotency_key <> ''),
    UNIQUE (user_id, origin, idempotency_key),
    FOREIGN KEY (user_id, subject_topic) REFERENCES topics (user_id, id),
    FOREIGN KEY (user_id, subject_entity) REFERENCES entities (user_id, id),
    CHECK (subject_topic IS NULL OR subject_entity IS NULL)
);

-- The timeline: one user's events, newest first.
CREATE INDEX events_timeline ON events (user_id, recorded_at DESC, id DESC);

ALTER TABLE events ENABLE ROW LEVEL SECURITY;
ALTER TABLE events FORCE ROW LEVEL SECURITY;
CREATE POLICY events_user ON events
    USING (user_id = nullif(current_setting('brain.user_id', true), '')::uuid);

-- 0001 granted SELECT, INSERT and UPDATE on every new table. The verbs are
-- read only to the service, and an event is append only: inserted and read,
-- never changed.
REVOKE INSERT, UPDATE ON verbs FROM brain_app;
REVOKE UPDATE ON events FROM brain_app;
