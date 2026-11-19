-- Decision issues: for each decision the agent marked for the owner on a card Dev flow merged, the
-- issue Dev flow opens as the app with the project's attention label, and whether it is still open.
-- How one is opened and listed is "The merge check" in devflow/README.md, and why
-- "The merge check" in devflow/docs/devflow-architecture.md.

-- One decision, recorded in the transaction that records its card's merge, so a merge never loses
-- one, and its issue once opened. Every text is data, the agent's and the reviewer's words as they
-- wrote them, shown as text.
CREATE TABLE decision_issues (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    card_number int NOT NULL CHECK (card_number > 0),
    card_title text NOT NULL,
    card_url text NOT NULL CHECK (card_url <> ''),
    pull_request_number int NOT NULL CHECK (pull_request_number > 0),
    pull_request_url text NOT NULL CHECK (pull_request_url <> ''),
    -- Its place among the card's decisions for the owner, from 0, which keeps one issue per
    -- decision.
    position int NOT NULL CHECK (position >= 0),
    chosen text NOT NULL CHECK (chosen <> ''),
    alternative text NOT NULL,
    why text NOT NULL,
    -- The reviewer's view on it, from the review that approved the merged commit; null when it gave
    -- none.
    reviewer_view text,
    -- The attention label it is opened with, the project's when it was recorded, which closing it is
    -- read by.
    label text NOT NULL CHECK (label <> ''),
    recorded_at timestamptz NOT NULL,
    -- The issue, null until it is opened; all three or none.
    issue_number int CHECK (issue_number > 0),
    issue_url text CHECK (issue_url <> ''),
    opened_at timestamptz,
    -- When a read of the project found it no longer open with its label; it leaves the list then.
    closed_at timestamptz,
    UNIQUE (project_id, pull_request_number, position),
    CHECK ((issue_number IS NULL) = (issue_url IS NULL) AND (issue_number IS NULL) = (opened_at IS NULL)),
    CHECK (closed_at IS NULL OR issue_number IS NOT NULL),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

-- The decisions still to open, and the open ones a read follows and the list shows.
CREATE INDEX decision_issues_unopened ON decision_issues (project_id) WHERE issue_number IS NULL;
CREATE INDEX decision_issues_open ON decision_issues (project_id) WHERE issue_number IS NOT NULL AND closed_at IS NULL;

ALTER TABLE decision_issues ENABLE ROW LEVEL SECURITY;
ALTER TABLE decision_issues FORCE ROW LEVEL SECURITY;
CREATE POLICY decision_issues_user ON decision_issues
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
