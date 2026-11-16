-- The snapshot of what waits for the owner in each project, and how each
-- project's last read went. Pages read these and never the code host, so they
-- are fast and work while the code host is down; a loop and a refresh fill
-- them. How reading works is "Reading and the snapshot" in
-- devflow/docs/devflow-architecture.md, and the tables are "What waits" in
-- devflow/README.md.

-- One pull request that is the owner's turn, as RunnerReader read it at
-- read_at. A read replaces the project's rows whole, in one transaction.
CREATE TABLE waiting_items (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    project_id uuid NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    number int NOT NULL CHECK (number > 0),
    kind text NOT NULL CHECK (kind IN ('waiting', 'stopped')),
    sent_back boolean NOT NULL,
    title text NOT NULL,
    web_url text NOT NULL,
    slice_number int,
    head_commit text NOT NULL,
    can_merge boolean NOT NULL,
    summary text NOT NULL,
    reason text NOT NULL,
    -- The decisions marked for the owner, each {"chosen", "alternative", "why"}.
    decisions jsonb NOT NULL CHECK (jsonb_typeof(decisions) = 'array'),
    other_decisions int NOT NULL CHECK (other_decisions >= 0),
    -- The reviewer's "For the owner" entries, each a string.
    reviewer_view jsonb NOT NULL CHECK (jsonb_typeof(reviewer_view) = 'array'),
    read_at timestamptz NOT NULL,
    UNIQUE (project_id, number)
);

ALTER TABLE waiting_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE waiting_items FORCE ROW LEVEL SECURITY;
CREATE POLICY waiting_items_user ON waiting_items
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- How a project's reads went: when the last one was tried, when one last
-- succeeded, null before any has, and why the last one failed, a short fixed
-- phrase and never the code host's own text, null after a success.
CREATE TABLE project_reads (
    project_id uuid PRIMARY KEY REFERENCES projects (id) ON DELETE CASCADE,
    user_id uuid NOT NULL,
    last_attempt_at timestamptz NOT NULL,
    last_success_at timestamptz,
    last_error text CHECK (last_error <> '')
);

ALTER TABLE project_reads ENABLE ROW LEVEL SECURITY;
ALTER TABLE project_reads FORCE ROW LEVEL SECURITY;
CREATE POLICY project_reads_user ON project_reads
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
