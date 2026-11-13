-- Searching memory by its words: what an item says and the evidence behind
-- it, each as a stored generated tsvector with a GIN index. The columns are
-- in "The tables" in docs/data-model.md, and the search that reads them is
-- `brain.retrieval.search`.
--
-- The configuration is english, written out on every call: stemming is what
-- lets `indexing` find `indexes`, and a configuration that came from a
-- setting would not be immutable, which a generated column must be.
--
-- Postgres computes both columns on every insert and on every update of the
-- row, so the app role, which may update only an item's status and
-- inactive_reason, never writes them and needs no grant for them.

-- An item's own words: its content, its level or focus, its category and
-- every text value in its details, a decision's reasoning and the options it
-- rejected among them.
ALTER TABLE items ADD COLUMN search tsvector GENERATED ALWAYS AS (
    to_tsvector('english'::regconfig, content)
    || to_tsvector('english'::regconfig, coalesce(value, ''))
    || to_tsvector('english'::regconfig, coalesce(category, ''))
    || jsonb_to_tsvector('english'::regconfig, details, '["string"]')
) STORED;

-- What an event says happened.
ALTER TABLE events ADD COLUMN search tsvector GENERATED ALWAYS AS (
    to_tsvector('english'::regconfig, evidence)
) STORED;

CREATE INDEX items_search ON items USING gin (search);
CREATE INDEX events_search ON events USING gin (search);
