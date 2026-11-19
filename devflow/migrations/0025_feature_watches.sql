-- Watching each open feature's branch as the default branch is watched: a watch per watched
-- branch, the branch each of Dev flow's own merges went into, the pushes Dev flow's own jobs made on
-- a feature's branch, and the pushes by the app read while a run on that branch was under way,
-- judged when the run ends. Why it is built so is "A feature's branch" under "Watching the default
-- branch" in devflow/docs/devflow-architecture.md, and the tables are "Watching branches" in
-- devflow/README.md. Each table follows its project, gone with it and renumbered with it, and is
-- guarded like every scoped table.

-- From one row per project to one per project and branch: the default branch's, with no feature,
-- and one for each open feature's branch, with the feature's id. Every row there is the default
-- branch's and is kept as it was, where it began and what it last judged.
ALTER TABLE branch_watches ADD COLUMN feature_id uuid;
ALTER TABLE branch_watches DROP CONSTRAINT branch_watches_pkey;
ALTER TABLE branch_watches ADD CONSTRAINT branch_watches_one_per_branch UNIQUE NULLS NOT DISTINCT (project_id, feature_id);
-- A feature's watch goes with its feature, which is removed only once no card refers to it.
ALTER TABLE branch_watches ADD FOREIGN KEY (project_id, feature_id) REFERENCES features (project_id, id) ON DELETE CASCADE ON UPDATE CASCADE;

-- The rows below are every user's, which the policies hide even from the owner while they are
-- forced; they are lifted for this transaction's own statements and forced again before it ends.
ALTER TABLE branch_watches NO FORCE ROW LEVEL SECURITY;
ALTER TABLE features NO FORCE ROW LEVEL SECURITY;
ALTER TABLE merges_made NO FORCE ROW LEVEL SECURITY;

-- A feature whose branch was made before this migration is watched from its next read, which
-- begins at the branch's creation as for one made after.
INSERT INTO branch_watches (project_id, user_id, feature_id, branch, last_activity_id, began_at, last_read_at, last_attempt_at, last_error)
SELECT project_id, user_id, id, branch, NULL, now(), now(), now(), NULL
FROM features
WHERE branch IS NOT NULL AND branch_base_commit IS NOT NULL AND state NOT IN ('merged', 'closed');

-- The branch each merge went into, so the watch of one branch never finds another's merge fine.
-- Every merge so far went into its project's default branch, as its watch names it; a project
-- whose watch never began made none, since Dev flow merges only while it is watched.
ALTER TABLE merges_made ADD COLUMN branch text CHECK (branch <> '');
UPDATE merges_made m
SET branch = coalesce((SELECT b.branch FROM branch_watches b WHERE b.project_id = m.project_id AND b.feature_id IS NULL), 'main');
ALTER TABLE merges_made ALTER COLUMN branch SET NOT NULL;

ALTER TABLE branch_watches FORCE ROW LEVEL SECURITY;
ALTER TABLE features FORCE ROW LEVEL SECURITY;
ALTER TABLE merges_made FORCE ROW LEVEL SECURITY;

-- A push one of Dev flow's own jobs made on a feature's branch, by the commit its run's result
-- named, written in the transaction that records the result, so the watch finds it fine when it
-- reads it.
CREATE TABLE pushes_made (
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    branch text NOT NULL CHECK (branch <> ''),
    commit text NOT NULL CHECK (commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    -- The run whose result named it.
    run_id uuid NOT NULL REFERENCES runs (id) ON DELETE CASCADE,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, branch, commit),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE pushes_made ENABLE ROW LEVEL SECURITY;
ALTER TABLE pushes_made FORCE ROW LEVEL SECURITY;
CREATE POLICY pushes_made_user ON pushes_made
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);

-- A push by the app on a feature's branch that the watch read while a run of Dev flow's on that
-- branch was under way, a row until the run has ended: then it is fine when the run's result
-- recorded its commit in pushes_made, and stops the project when it did not.
CREATE TABLE pushes_awaited (
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    -- The code host's id of the change, which stays the same.
    activity_id bigint NOT NULL,
    branch text NOT NULL CHECK (branch <> ''),
    -- The run that was under way on the branch when it was read.
    run_id uuid NOT NULL REFERENCES runs (id) ON DELETE CASCADE,
    -- Who pushed, data, as the code host named it.
    actor text NOT NULL CHECK (actor <> ''),
    -- The commit the branch was at after it.
    commit text NOT NULL CHECK (commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$'),
    landed_at timestamptz NOT NULL,
    read_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, activity_id),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE pushes_awaited ENABLE ROW LEVEL SECURITY;
ALTER TABLE pushes_awaited FORCE ROW LEVEL SECURITY;
CREATE POLICY pushes_awaited_user ON pushes_awaited
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
