-- Cards: each project's open issues carrying its pick-up label, as each read of the project found
-- them, and where each stands. How a card is picked is "Cards" in
-- devflow/docs/devflow-architecture.md, and the table is "Cards" in devflow/README.md.

-- One card. A read inserts and updates the project's cards in the same transaction as its waiting
-- items; a card that has not started and is no longer labelled is deleted, and one that has
-- started finishes first, marked unlabelled.
CREATE TABLE cards (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    number int NOT NULL CHECK (number > 0),
    -- The issue's title and address as last read; data, shown as text.
    title text NOT NULL,
    web_url text NOT NULL,
    -- The design's states, and held and ignored, which picking sets. queued, held and ignored
    -- have not started; every other has.
    state text NOT NULL CHECK (state IN ('queued', 'building', 'checking', 'reviewing', 'fixing',
                                         'waiting_for_owner', 'done', 'stopped', 'held', 'ignored')),
    -- Why it is where it is, a sentence for the page, or null when there is nothing to say.
    reason text CHECK (reason <> ''),
    -- What it is blocked by that is still open, as #12 or owner/name#12; a queued card is free to
    -- start only when this is empty.
    waits_for text[] NOT NULL DEFAULT '{}',
    priority text NOT NULL CHECK (priority IN ('high', 'none', 'low')),
    -- When the issue was written, which orders cards of the same priority, oldest first.
    issue_created_at timestamptz NOT NULL,
    -- The record of what was picked, which later parts compare against before building on it and
    -- before merging: the body's SHA-256, the title, and the id of the edit history's newest
    -- entry, null when it was never edited. Null only for a card that was never picked.
    picked_body_sha256 bytea CHECK (octet_length(picked_body_sha256) = 32),
    picked_title text,
    picked_edit text CHECK (picked_edit <> ''),
    -- Its label was taken off while it ran; it is removed once its run ends.
    unlabelled boolean NOT NULL DEFAULT false,
    -- When its state, its reason or what it waits for last changed, which the list is ordered
    -- by, newest first.
    changed_at timestamptz NOT NULL,
    read_at timestamptz NOT NULL,
    UNIQUE (project_id, number),
    CHECK ((picked_body_sha256 IS NULL) = (picked_title IS NULL)),
    CHECK (state = 'ignored' OR picked_body_sha256 IS NOT NULL),
    -- Follows the project, as project_settings does.
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE cards ENABLE ROW LEVEL SECURITY;
ALTER TABLE cards FORCE ROW LEVEL SECURITY;
CREATE POLICY cards_user ON cards
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
