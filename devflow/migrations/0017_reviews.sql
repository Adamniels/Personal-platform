-- Reviews: each verdict a reviewer handed back, kept by Dev flow for the commit the worker
-- reviewed. How a review is recorded and posted is "Review" in devflow/README.md, and why a verdict
-- lives here and is tied to its commit "Review" in devflow/docs/devflow-architecture.md.

-- One verdict, recorded with the review run that read it, in the same transaction as its result.
-- It names its card by number, as runs do, so a card removed when its label is taken off leaves its
-- reviews behind. Its usage is its run's.
CREATE TABLE reviews (
    -- The review run, which holds its usage, and of which this is the verdict.
    run_id uuid PRIMARY KEY,
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    card_number int NOT NULL CHECK (card_number > 0),
    -- The commit the worker checked out and reviewed, the run's; never a commit the agent named.
    reviewed_commit text NOT NULL CHECK (reviewed_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    verdict text NOT NULL CHECK (verdict IN ('approve', 'changes')),
    -- The reviewer's words, data, shown as text: its summary, its findings and its view on each
    -- decision for the owner, as the verdict's form holds them.
    summary text NOT NULL,
    findings jsonb NOT NULL CHECK (jsonb_typeof(findings) = 'array'),
    for_owner jsonb NOT NULL CHECK (jsonb_typeof(for_owner) = 'array'),
    -- The pull request it is posted on, the card's when it was recorded.
    pull_request_number int NOT NULL CHECK (pull_request_number > 0),
    reviewed_at timestamptz NOT NULL,
    -- When the app's comment, a copy for people, was posted; null until it is. Nothing reads the
    -- comment back.
    posted_at timestamptz,
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (run_id) REFERENCES runs (id) ON DELETE CASCADE
);

-- A card's reviews, which its page and its current verdict read.
CREATE INDEX reviews_card ON reviews (project_id, card_number, reviewed_at);

-- The reviews still to post, which the loop looks through each minute.
CREATE INDEX reviews_unposted ON reviews (project_id) WHERE posted_at IS NULL;

ALTER TABLE reviews ENABLE ROW LEVEL SECURITY;
ALTER TABLE reviews FORCE ROW LEVEL SECURITY;
CREATE POLICY reviews_user ON reviews
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
