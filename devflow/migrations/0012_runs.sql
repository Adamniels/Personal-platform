-- Runs: each job a worker was handed, and what came back. How a job is handed out and a result
-- recorded is "Jobs" in devflow/README.md, and why it is built so "Handing out work" in
-- devflow/docs/devflow-architecture.md.

-- The body of a card as it was picked, which the brief quotes, so a job carries exactly the text
-- whose author and edits picking checked, and handing out reads nothing from GitHub to write it.
-- Null for a card never picked, and for one picked before this column, until its next read; such a
-- card is not handed out.
ALTER TABLE cards ADD COLUMN picked_body text;
ALTER TABLE cards ADD CHECK (picked_body IS NULL OR picked_body_sha256 IS NOT NULL);

-- One run: a job handed to a worker, from when it was handed out to when its result came back or
-- it was found lost. It names its card by number rather than by a key to cards, so a card removed
-- when its label is taken off leaves its runs, and their usage, behind.
CREATE TABLE runs (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    card_number int NOT NULL CHECK (card_number > 0),
    -- The worker it was handed to, which alone may report on it.
    worker_id uuid NOT NULL,
    kind text NOT NULL CHECK (kind IN ('build')),
    branch text NOT NULL CHECK (branch <> '' AND length(branch) <= 255),
    base text NOT NULL CHECK (base <> '' AND length(base) <= 255),
    started_at timestamptz NOT NULL,
    -- When the worker said the check had begun, null before.
    checking_at timestamptz,
    -- When the result came back, or the run was found lost; null while it runs.
    finished_at timestamptz,
    -- How it ended, null while it runs.
    result text CHECK (result IN ('succeeded', 'stopped', 'check_failed', 'failed_in_card', 'failed_outside_card',
                                  'report_invalid', 'lost')),
    -- Why it ended so, a sentence for the page; null on a success.
    reason text CHECK (reason <> ''),
    -- What the worker sent, as it came.
    report jsonb,
    check_exit_code int,
    check_output text,
    head_commit text CHECK (head_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    auth_method text CHECK (length(auth_method) <= 255),
    -- Claude's usage as it came, and the figures the usage page reads from it, each null when
    -- Claude gave none.
    usage jsonb,
    input_tokens bigint,
    output_tokens bigint,
    cache_creation_input_tokens bigint,
    cache_read_input_tokens bigint,
    cost_usd numeric(12, 6),
    turns int,
    -- How long the job took, as the worker measured it.
    duration_ms bigint CHECK (duration_ms >= 0),
    stderr text,
    CHECK ((finished_at IS NULL) = (result IS NULL)),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (project_id, worker_id) REFERENCES workers (project_id, id) ON DELETE CASCADE
);

-- A card has at most one run under way, whatever the code does: the lock that hands a card out is
-- the first guard, and this the second.
CREATE UNIQUE INDEX runs_one_under_way ON runs (project_id, card_number) WHERE finished_at IS NULL;

-- The runs under way, which the loop looks through for lost ones.
CREATE INDEX runs_under_way ON runs (project_id, started_at) WHERE finished_at IS NULL;

ALTER TABLE runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE runs FORCE ROW LEVEL SECURITY;
CREATE POLICY runs_user ON runs
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
