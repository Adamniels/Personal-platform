-- Features: each project's open issues carrying its pick-up label that have sub-issues, as each
-- read of the project found them, and where each stands; and on cards, the feature each belongs
-- to and its place in it. How a feature is picked is "Features and their cards" in
-- devflow/docs/devflow-architecture.md, and the table is "Features" in devflow/README.md.

-- One feature, its parent issue, in its final shape for part 4: a read inserts and updates the
-- project's features in the same transaction as its cards and waiting items. This part's first
-- slice sets only queued, held and ignored; the later slices of the feature the other states, the
-- branch, the commits, the pull request and the fix round.
CREATE TABLE features (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    number int NOT NULL CHECK (number > 0),
    -- The parent's title and address as last read; data, shown as text.
    title text NOT NULL,
    web_url text NOT NULL,
    -- queued, held and ignored are picking's; every other is a later slice's.
    state text NOT NULL CHECK (state IN ('queued', 'building', 'bringing_in', 'resolving', 'reviewing', 'fixing',
                                         'waiting_for_owner', 'ready', 'merged', 'closed', 'held', 'ignored')),
    -- Why it is where it is, a sentence for the page, or null when there is nothing to say.
    reason text CHECK (reason <> ''),
    -- The parent's priority label, which orders features with cards on their own.
    priority text NOT NULL CHECK (priority IN ('high', 'none', 'low')),
    -- When the parent was written, which orders features of the same priority, oldest first.
    issue_created_at timestamptz NOT NULL,
    -- The record of what was picked, as a card's: the body's SHA-256, the title, the id of the edit
    -- history's newest entry, null when it was never edited, and the body, which later slices
    -- quote in every brief. Null only for a feature that was never picked.
    picked_body_sha256 bytea CHECK (octet_length(picked_body_sha256) = 32),
    picked_title text,
    picked_edit text CHECK (picked_edit <> ''),
    picked_body text,
    -- Its label was taken off while a card of it ran; it is removed once none of its cards is left.
    unlabelled boolean NOT NULL DEFAULT false,
    -- The feature's branch, the default branch's commit it was made from, and the default branch's
    -- commit last brought into it; null before each.
    branch text CHECK (branch <> '' AND length(branch) <= 255),
    branch_base_commit text CHECK (branch_base_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    brought_in_commit text CHECK (brought_in_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    -- The feature's pull request to the default branch, null before it is opened.
    pull_request_number int CHECK (pull_request_number > 0),
    -- The fix rounds of the whole feature used, 0 before any.
    fix_round int NOT NULL DEFAULT 0 CHECK (fix_round BETWEEN 0 AND 3),
    -- When its state or its reason last changed.
    changed_at timestamptz NOT NULL,
    read_at timestamptz NOT NULL,
    UNIQUE (project_id, number),
    -- What a card's feature refers to, so a card never belongs to another project's feature.
    UNIQUE (project_id, id),
    CHECK ((picked_body_sha256 IS NULL) = (picked_title IS NULL)),
    CHECK (picked_body IS NULL OR picked_body_sha256 IS NOT NULL),
    CHECK (state = 'ignored' OR picked_body_sha256 IS NOT NULL),
    -- Follows the project, as cards does.
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE features ENABLE ROW LEVEL SECURITY;
ALTER TABLE features FORCE ROW LEVEL SECURITY;
CREATE POLICY features_user ON features
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- A card's feature, null for a card on its own, and its place among the feature's cards, 1 first,
-- in the sub-issue order; set exactly when the feature is. A feature is removed only once no card
-- refers to it, so the reference has no action of its own. The positions are unique per feature
-- at the end of each read's transaction, which moves cards past each other.
ALTER TABLE cards ADD COLUMN feature_id uuid;
ALTER TABLE cards ADD COLUMN position int CHECK (position > 0);
ALTER TABLE cards ADD CHECK ((feature_id IS NULL) = (position IS NULL));
ALTER TABLE cards ADD FOREIGN KEY (project_id, feature_id) REFERENCES features (project_id, id) ON UPDATE CASCADE;
ALTER TABLE cards ADD CONSTRAINT cards_feature_position UNIQUE (feature_id, position) DEFERRABLE INITIALLY DEFERRED;
