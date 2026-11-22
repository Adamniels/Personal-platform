-- The setup run: a project whose default branch has no valid .devflow/ is set up by an agent, on a
-- worker started from the base image itself, which writes .devflow/Dockerfile and
-- .devflow/project.json on a branch of its own; its pull request is reviewed and fixed like a
-- card's, and always waits for the owner to merge it. How it is handed out, judged and shown is
-- "Setting up a project" in devflow/README.md, and why it is built so "Setting up a project" in
-- devflow/docs/devflow-architecture.md.

-- What the loop last read of .devflow/ on the default branch: whether both files are there and
-- valid, the recipe by the recipe route's rules and project.json by its schema, and when they are
-- not, why. Null before the loop has read it, which hands out no setup on a guess.
ALTER TABLE projects ADD COLUMN devflow_valid boolean;
ALTER TABLE projects ADD COLUMN devflow_problem text CHECK (devflow_problem <> '' AND length(devflow_problem) <= 1000);
ALTER TABLE projects ADD CONSTRAINT projects_devflow_problem_when_invalid
    CHECK ((devflow_problem IS NOT NULL) = (devflow_valid IS FALSE));

-- One row per setup of a project, numbered from 1: the first on devflow/setup, each later one on
-- devflow/setup-<k>, so a branch whose pull request closed is never used again. Its state, as a
-- card's: queued, to be run; checking, its run succeeded and its pull request opens next;
-- reviewing, fixing and waiting_for_owner, as a card's pull request; stopped, with why; done, its
-- pull request merged. A run under way is read from runs, never stored here, so a lost run leaves
-- nothing to put back. fix_rounds counts the review rounds used; fix_round, fix_review_id and
-- fix_comments are what its next fix answers, as a card's; comments_read_through is the newest
-- comment on its pull request already taken; failures counts failures in a row, as a card's
-- in_card_failures; body_run_id is the run whose report its pull request's body was written from.
CREATE TABLE setups (
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    number int NOT NULL CHECK (number >= 1),
    branch text NOT NULL CHECK (branch ~ '^devflow/setup(-[0-9]+)?$'),
    state text NOT NULL CHECK (state IN ('queued', 'checking', 'reviewing', 'fixing', 'waiting_for_owner', 'stopped', 'done')),
    reason text CHECK (reason <> '' AND length(reason) <= 2000),
    pull_request_number int CHECK (pull_request_number > 0),
    pull_request_url text,
    fix_rounds int NOT NULL DEFAULT 0 CHECK (fix_rounds BETWEEN 0 AND 10),
    fix_round int CHECK (fix_round BETWEEN 1 AND 10),
    fix_review_id uuid,
    fix_comments jsonb CHECK (fix_comments IS NULL OR jsonb_typeof(fix_comments) = 'array'),
    comments_read_through bigint,
    failures int NOT NULL DEFAULT 0 CHECK (failures >= 0),
    body_run_id uuid,
    created_at timestamptz NOT NULL,
    changed_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, number),
    CHECK ((pull_request_number IS NULL) = (pull_request_url IS NULL)),
    -- A fix may run with no pull request: the owner's comment runs one again on a setup that stopped
    -- before it had one, or whose pull request was closed, and its pull request opens once it is valid.
    CHECK (state NOT IN ('reviewing', 'waiting_for_owner', 'done') OR pull_request_number IS NOT NULL),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

-- One setup of a project that is not done at a time, whatever the code does.
CREATE UNIQUE INDEX setups_one_open ON setups (project_id) WHERE state <> 'done';

ALTER TABLE setups ENABLE ROW LEVEL SECURITY;
ALTER TABLE setups FORCE ROW LEVEL SECURITY;
CREATE POLICY setups_user ON setups
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- A setup's runs: setup, its first run or a fix of its pull request, which pushes to its branch,
-- and setup_review, its review. Each names its setup, and no card or feature.
ALTER TABLE runs ADD COLUMN setup_number int;
ALTER TABLE runs ADD CONSTRAINT runs_setup_fk FOREIGN KEY (project_id, setup_number) REFERENCES setups (project_id, number) ON DELETE CASCADE;
ALTER TABLE runs DROP CONSTRAINT runs_kind_check;
ALTER TABLE runs ADD CONSTRAINT runs_kind_check
    CHECK (kind IN ('build', 'review', 'fix', 'conflict', 'feature_review', 'feature_fix', 'read', 'check', 'setup', 'setup_review'));
ALTER TABLE runs DROP CONSTRAINT runs_card_or_feature;
ALTER TABLE runs ADD CONSTRAINT runs_card_or_feature CHECK (
    CASE WHEN kind IN ('check', 'setup', 'setup_review') THEN card_number IS NULL AND feature_number IS NULL
         ELSE (card_number IS NULL) <> (feature_number IS NULL) END);
ALTER TABLE runs ADD CONSTRAINT runs_setup_names_its_setup CHECK ((kind IN ('setup', 'setup_review')) = (setup_number IS NOT NULL));
ALTER TABLE runs ADD CHECK (kind <> 'setup_review' OR head_commit IS NOT NULL);

-- One run of a setup under way at a time, beside the lock the claim takes.
CREATE UNIQUE INDEX runs_one_under_way_per_setup ON runs (project_id, setup_number) WHERE finished_at IS NULL;
