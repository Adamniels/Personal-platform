-- Stops: a card that cannot be made to work stops and is marked on GitHub, a comment from the owner
-- runs it again, and a failure outside the card makes its project wait rather than count against
-- the card. What stops a card and how it runs again is "Stops" in devflow/README.md, and why a
-- failure outside never counts "Stops" in devflow/docs/devflow-architecture.md.

-- How many runs in a row failed in the card, 0 or 1, since its last success or since it last ran
-- again; and the commit its branch was at after the last of them, null when nothing was pushed. A
-- second failure in the card with the branch still there stops it.
ALTER TABLE cards ADD COLUMN in_card_failures int NOT NULL DEFAULT 0 CHECK (in_card_failures BETWEEN 0 AND 1);
ALTER TABLE cards ADD COLUMN failed_at_commit text CHECK (failed_at_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');

-- When the card last stopped, set as it moves to stopped; null for a card that never did. A
-- comment of the owner's runs it again only when it is newer than this.
ALTER TABLE cards ADD COLUMN stopped_at timestamptz;

-- When the app marked the stop on the card's issue, with the attention label and its comment; null
-- while that waits, and again for each new stop.
ALTER TABLE cards ADD COLUMN stop_marked_at timestamptz;

-- Whether the app put the project's attention label on the card's issue and has not taken it off
-- since, so it is taken off once the card runs again.
ALTER TABLE cards ADD COLUMN attention_labelled boolean NOT NULL DEFAULT false;

-- Whether a run ended at its time limit, as the worker said; a run that did stops its card at once.
ALTER TABLE runs ADD COLUMN timed_out boolean NOT NULL DEFAULT false;

-- When Claude's usage limit resets, as the worker read it from Claude's answer to a run that failed
-- outside the card; null when the answer gave none.
ALTER TABLE runs ADD COLUMN resets_at timestamptz;

-- A project that waits after failures outside its cards: how many runs in a row failed outside a
-- card or were lost, the last one's reason, and until when it hands out no job. A row only while
-- such failures are counted; the next run that succeeds removes it.
CREATE TABLE project_waits (
    project_id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    failures int NOT NULL CHECK (failures > 0),
    reason text NOT NULL,
    wait_until timestamptz NOT NULL,
    -- When the first of them failed.
    since timestamptz NOT NULL,
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE project_waits ENABLE ROW LEVEL SECURITY;
ALTER TABLE project_waits FORCE ROW LEVEL SECURITY;
CREATE POLICY project_waits_user ON project_waits
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
