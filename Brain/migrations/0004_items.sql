-- The memory store: items, what they are about, how they relate and the
-- events behind them. The shape is "The tables", "Which kind goes in which
-- store" and "Versions" in docs/data-model.md; who may change what a store
-- holds is "Authority follows the store, not the source" in
-- docs/trust-model.md.
--
-- The four tables arrive together because they are one design: every kind
-- the data model names is allowed here already, so a later slice adds a kind
-- or a behaviour without a migration.
--
-- As in 0002, every reference between rows carries user_id, so it can only
-- name a row of the same user, and no column has a default.

-- What an item's created_by_event and its evidence point at.
ALTER TABLE events ADD UNIQUE (user_id, id);

CREATE TABLE items (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    -- Shared by every version of one entry: the first version's id.
    lineage_id uuid NOT NULL,
    kind text NOT NULL CHECK (
        kind IN ('profile', 'rule', 'topic_level', 'topic_focus', 'state',
                 'history', 'decision')
    ),
    store text NOT NULL CHECK (
        store IN ('profile', 'procedural', 'knowledge', 'semantic',
                  'reported_history')
    ),
    nature text NOT NULL CHECK (nature IN ('claim', 'record')),
    provenance text NOT NULL CHECK (provenance IN ('asserted', 'derived')),
    content text NOT NULL CHECK (btrim(content, E' \t\n\r') <> ''),
    -- A level or a focus, for the kinds that carry one.
    value text CHECK (value <> ''),
    -- A profile entry's category, and only a profile entry's.
    category text CHECK (
        category IN ('identity', 'goals', 'values', 'interests', 'preferences',
                     'working_style')
    ),
    details jsonb NOT NULL CHECK (jsonb_typeof(details) = 'object'),
    -- When it happened, for history and decisions alone.
    occurred_at timestamptz,
    occurred_precision text CHECK (
        occurred_precision IN ('instant', 'day', 'month', 'year')
    ),
    status text NOT NULL CHECK (status IN ('active', 'inactive')),
    inactive_reason text CHECK (inactive_reason IN ('superseded', 'retired')),
    created_at timestamptz NOT NULL,
    created_by_event uuid NOT NULL,
    -- What the references below and in the other three tables point at.
    UNIQUE (user_id, id),
    FOREIGN KEY (user_id, lineage_id) REFERENCES items (user_id, id),
    FOREIGN KEY (user_id, created_by_event) REFERENCES events (user_id, id),
    -- The store is decided by the kind, and so is the nature: the table in
    -- "Which kind goes in which store". The store is what decides who may
    -- change a claim, so it is never left to the code alone.
    CHECK (
        (kind, store, nature) IN (
            ('profile', 'profile', 'claim'),
            ('topic_focus', 'profile', 'claim'),
            ('rule', 'procedural', 'claim'),
            ('topic_level', 'knowledge', 'claim'),
            ('state', 'semantic', 'claim'),
            ('history', 'reported_history', 'record'),
            ('decision', 'semantic', 'claim')
        )
    ),
    CHECK ((kind = 'profile') = (category IS NOT NULL)),
    CHECK ((occurred_at IS NULL) = (occurred_precision IS NULL)),
    CHECK (occurred_at IS NULL OR kind IN ('history', 'decision')),
    CHECK ((status = 'inactive') = (inactive_reason IS NOT NULL))
);

-- An entry's history, every version of its lineage.
CREATE INDEX items_lineage ON items (user_id, lineage_id);
-- The list, one user's items newest first.
CREATE INDEX items_listing ON items (user_id, created_at DESC, id DESC);

-- What an item is about, a topic or a named thing, one per row. An item with
-- no rows here is global, as every rule is. A topic is kept as it was named,
-- an alias included, and read through it, as an event's is.
CREATE TABLE item_subjects (
    item_id uuid NOT NULL,
    user_id uuid NOT NULL,
    topic_id uuid,
    entity_id uuid,
    FOREIGN KEY (user_id, item_id) REFERENCES items (user_id, id),
    FOREIGN KEY (user_id, topic_id) REFERENCES topics (user_id, id),
    FOREIGN KEY (user_id, entity_id) REFERENCES entities (user_id, id),
    CHECK ((topic_id IS NULL) <> (entity_id IS NULL))
);

CREATE UNIQUE INDEX item_subjects_topic
    ON item_subjects (item_id, topic_id) WHERE topic_id IS NOT NULL;
CREATE UNIQUE INDEX item_subjects_entity
    ON item_subjects (item_id, entity_id) WHERE entity_id IS NOT NULL;
-- Listing by subject.
CREATE INDEX item_subjects_by_topic ON item_subjects (user_id, topic_id);
CREATE INDEX item_subjects_by_entity ON item_subjects (user_id, entity_id);

-- kind is text, checked in brain.items against its list, as topic relations
-- are, so a new kind is a code change and not a migration.
CREATE TABLE item_relations (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    from_item uuid NOT NULL,
    to_item uuid NOT NULL,
    kind text NOT NULL CHECK (kind <> ''),
    created_at timestamptz NOT NULL,
    -- Null while it holds.
    ended_at timestamptz,
    created_by_event uuid NOT NULL,
    FOREIGN KEY (user_id, from_item) REFERENCES items (user_id, id),
    FOREIGN KEY (user_id, to_item) REFERENCES items (user_id, id),
    FOREIGN KEY (user_id, created_by_event) REFERENCES events (user_id, id),
    CHECK (from_item <> to_item),
    CHECK (ended_at IS NULL OR ended_at >= created_at)
);

CREATE UNIQUE INDEX item_relations_holding
    ON item_relations (user_id, from_item, to_item, kind)
    WHERE ended_at IS NULL;
CREATE INDEX item_relations_to ON item_relations (user_id, to_item);

-- The events behind an item, for it or against it: what confidence and
-- currency are computed from at read time, and never stored.
CREATE TABLE item_evidence (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    item_id uuid NOT NULL,
    event_id uuid NOT NULL,
    kind text NOT NULL CHECK (kind IN ('supports', 'contradicts')),
    created_at timestamptz NOT NULL,
    FOREIGN KEY (user_id, item_id) REFERENCES items (user_id, id),
    FOREIGN KEY (user_id, event_id) REFERENCES events (user_id, id),
    -- An event bears on an item once, one way.
    UNIQUE (user_id, item_id, event_id)
);

-- An event about a memory item, for what the owner did to it: the reference
-- 0003 left for this migration, beside the topic's and the named thing's.
ALTER TABLE events
    ADD FOREIGN KEY (user_id, subject_item) REFERENCES items (user_id, id);

ALTER TABLE items ENABLE ROW LEVEL SECURITY;
ALTER TABLE items FORCE ROW LEVEL SECURITY;
CREATE POLICY items_user ON items
    USING (user_id = nullif(current_setting('brain.user_id', true), '')::uuid);

ALTER TABLE item_subjects ENABLE ROW LEVEL SECURITY;
ALTER TABLE item_subjects FORCE ROW LEVEL SECURITY;
CREATE POLICY item_subjects_user ON item_subjects
    USING (user_id = nullif(current_setting('brain.user_id', true), '')::uuid);

ALTER TABLE item_relations ENABLE ROW LEVEL SECURITY;
ALTER TABLE item_relations FORCE ROW LEVEL SECURITY;
CREATE POLICY item_relations_user ON item_relations
    USING (user_id = nullif(current_setting('brain.user_id', true), '')::uuid);

ALTER TABLE item_evidence ENABLE ROW LEVEL SECURITY;
ALTER TABLE item_evidence FORCE ROW LEVEL SECURITY;
CREATE POLICY item_evidence_user ON item_evidence
    USING (user_id = nullif(current_setting('brain.user_id', true), '')::uuid);

-- 0001 granted SELECT, INSERT and UPDATE on every new table. The app role may
-- insert and read all four, and change only whether an item is active and
-- why: content is never changed in place, an edit is a new version, see
-- "Versions" in docs/data-model.md.
REVOKE UPDATE ON items, item_subjects, item_relations, item_evidence FROM brain_app;
GRANT UPDATE (status, inactive_reason) ON items TO brain_app;
