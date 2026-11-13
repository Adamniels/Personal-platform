-- The brain's subjects: topics, the relations between them, and named things.
-- The shape is "The tables" in docs/data-model.md, and what a subject is, is
-- "Scope is topic and entity, never feature" in docs/trust-model.md.
--
-- Each table is scoped to a user: user_id, row level security enabled and
-- forced, and the door's one policy, the expression brain.db.CURRENT_USER_SQL
-- holds. No column has a default: the brain mints every id, a UUIDv7, and
-- passes every time in.
--
-- Every reference between rows carries user_id, so it can only name a row of
-- the same user. A foreign key is checked without row level security, so one
-- on id alone would accept another user's topic.

CREATE TABLE topics (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    -- Lowercase letters, digits and hyphens, starting with a letter or digit,
    -- 1 to 80 characters. The API checks the same rule first, to answer 422.
    slug text NOT NULL CHECK (slug ~ '^[a-z0-9][a-z0-9-]{0,79}$'),
    name text NOT NULL CHECK (name <> ''),
    state text NOT NULL CHECK (state IN ('canonical', 'provisional')),
    -- The topic this one was marked the same as, or null. An alias never
    -- points at another alias; brain.topics checks that under the user's lock,
    -- since a check constraint cannot read another row.
    alias_of uuid,
    created_at timestamptz NOT NULL,
    UNIQUE (user_id, slug),
    -- What the references below point at.
    UNIQUE (user_id, id),
    FOREIGN KEY (user_id, alias_of) REFERENCES topics (user_id, id),
    CHECK (alias_of <> id)
);

-- kind is text, checked in brain.topics against its list, so a new kind is a
-- code change and not a migration. from_topic is broader than to_topic.
CREATE TABLE topic_relations (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    from_topic uuid NOT NULL,
    to_topic uuid NOT NULL,
    kind text NOT NULL CHECK (kind <> ''),
    created_at timestamptz NOT NULL,
    -- Null while it holds. A relation is ended, never deleted.
    ended_at timestamptz,
    FOREIGN KEY (user_id, from_topic) REFERENCES topics (user_id, id),
    FOREIGN KEY (user_id, to_topic) REFERENCES topics (user_id, id),
    CHECK (from_topic <> to_topic),
    CHECK (ended_at IS NULL OR ended_at >= created_at)
);

-- One relation of a kind between two topics holds at a time; an ended one
-- stays beside a new one.
CREATE UNIQUE INDEX topic_relations_holding
    ON topic_relations (user_id, from_topic, to_topic, kind)
    WHERE ended_at IS NULL;

-- A name, compared without letter case, and a free text label such as
-- 'project'. Only a name, never a link into a feature's data.
CREATE TABLE entities (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    name text NOT NULL CHECK (name <> ''),
    label text CHECK (label <> ''),
    created_at timestamptz NOT NULL
);

CREATE UNIQUE INDEX entities_name ON entities (user_id, lower(name));

ALTER TABLE topics ENABLE ROW LEVEL SECURITY;
ALTER TABLE topics FORCE ROW LEVEL SECURITY;
CREATE POLICY topics_user ON topics
    USING (user_id = nullif(current_setting('brain.user_id', true), '')::uuid);

ALTER TABLE topic_relations ENABLE ROW LEVEL SECURITY;
ALTER TABLE topic_relations FORCE ROW LEVEL SECURITY;
CREATE POLICY topic_relations_user ON topic_relations
    USING (user_id = nullif(current_setting('brain.user_id', true), '')::uuid);

ALTER TABLE entities ENABLE ROW LEVEL SECURITY;
ALTER TABLE entities FORCE ROW LEVEL SECURITY;
CREATE POLICY entities_user ON entities
    USING (user_id = nullif(current_setting('brain.user_id', true), '')::uuid);

-- 0001 granted SELECT, INSERT and UPDATE on every new table. The app role
-- updates only where a table says so, "Two roles" in docs/data-model.md: a
-- topic's state, when it is promoted, and its alias_of, when it is marked the
-- same as another or separated; and when a relation ended. A named thing is
-- never changed.
REVOKE UPDATE ON topics, topic_relations, entities FROM brain_app;
GRANT UPDATE (state, alias_of) ON topics TO brain_app;
GRANT UPDATE (ended_at) ON topic_relations TO brain_app;
