-- Reviewing the whole feature: once a feature's pull request is open, a fresh reviewer on a worker
-- of the project reviews its head, in a run for the feature, and the verdict is kept here for that
-- commit; the feature is ready for the owner when the latest review approves its head, or a head
-- that differs from it only by clean merges of the default branch, and CI passed there. How it is
-- handed out, judged, kept and posted is "Reviewing a feature" in devflow/README.md, and why it is
-- built so is "Reviewing a feature" in devflow/docs/devflow-architecture.md.

-- A run is a build, a review, a fix, a conflict's resolution, or a whole feature's review, which is
-- always a feature's and always names the commit it reviews from the start, as a card's review does.
ALTER TABLE runs DROP CONSTRAINT runs_kind_check;
ALTER TABLE runs ADD CONSTRAINT runs_kind_check CHECK (kind IN ('build', 'review', 'fix', 'conflict', 'feature_review'));
ALTER TABLE runs ADD CHECK (kind <> 'feature_review' OR (feature_number IS NOT NULL AND head_commit IS NOT NULL));

-- One verdict on a whole feature, recorded with the review run that read it, in the same
-- transaction as its result, as reviews holds a card's. It names its feature by its parent's
-- number, as runs do, so a feature removed when its label is taken off leaves its reviews behind.
-- Its usage is its run's.
CREATE TABLE feature_reviews (
    -- The review run, which holds its usage, and of which this is the verdict.
    run_id uuid PRIMARY KEY,
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    feature_number int NOT NULL CHECK (feature_number > 0),
    -- The commit the worker checked out and reviewed, the run's; never a commit the agent named.
    reviewed_commit text NOT NULL CHECK (reviewed_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    verdict text NOT NULL CHECK (verdict IN ('approve', 'changes')),
    -- The reviewer's words, data, shown as text: its summary, its findings and its view on each
    -- decision for the owner, as the verdict's form holds them.
    summary text NOT NULL,
    findings jsonb NOT NULL CHECK (jsonb_typeof(findings) = 'array'),
    for_owner jsonb NOT NULL CHECK (jsonb_typeof(for_owner) = 'array'),
    -- The feature's pull request it is posted on, the one it had when it was recorded.
    pull_request_number int NOT NULL CHECK (pull_request_number > 0),
    reviewed_at timestamptz NOT NULL,
    -- When the app's comment, a copy for people, was posted; null until it is. Nothing reads the
    -- comment back.
    posted_at timestamptz,
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (run_id) REFERENCES runs (id) ON DELETE CASCADE
);

-- A feature's reviews, which its page, its readiness and its next review read.
CREATE INDEX feature_reviews_feature ON feature_reviews (project_id, feature_number, reviewed_at);

-- The reviews still to post, which the loop looks through each minute.
CREATE INDEX feature_reviews_unposted ON feature_reviews (project_id) WHERE posted_at IS NULL;

ALTER TABLE feature_reviews ENABLE ROW LEVEL SECURITY;
ALTER TABLE feature_reviews FORCE ROW LEVEL SECURITY;
CREATE POLICY feature_reviews_user ON feature_reviews
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- The commit a feature reviewing is to be reviewed at, its branch's head as its watch named it when
-- the minute found no review covering it; null otherwise. Its review is handed out only while the
-- branch is still there.
ALTER TABLE features ADD COLUMN review_commit text CHECK (review_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');

-- The commit at which the feature's review last failed in the job, null otherwise: a review that
-- cannot be made is not handed out again for that commit, since the owner reviews the feature
-- anyway, and is once the branch moves.
ALTER TABLE features ADD COLUMN review_failed_at text CHECK (review_failed_at ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');

-- The first parent of a merge Dev flow made that merged no pull request, the commit of the
-- feature's branch the default branch was brought into, as the code host's answer named it; null
-- for a merge of a pull request, and when the answer named none. A review of that parent carries
-- over to the merge, which only brought the default branch in.
ALTER TABLE merges_made ADD COLUMN parent_commit text CHECK (parent_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');
