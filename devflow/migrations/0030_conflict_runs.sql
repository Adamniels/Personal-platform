-- Resolving a conflict: when the default branch conflicts with a feature's branch, an agent on a
-- worker of the project brings it in, in a run for the feature rather than for a card. How a
-- conflict job is handed out and judged is "A conflict" under "The end of a feature" in
-- devflow/README.md, and why it is built so is "A conflict" in devflow/docs/devflow-architecture.md.

-- A run is for a card or for a feature: exactly one of card_number and feature_number is set. A
-- feature's run names its parent by number, as a card's names its card, so a feature removed when
-- its label is taken off leaves its runs, and their usage, behind.
ALTER TABLE runs ALTER COLUMN card_number DROP NOT NULL;
ALTER TABLE runs ADD COLUMN feature_number int CHECK (feature_number > 0);
ALTER TABLE runs ADD CONSTRAINT runs_card_or_feature CHECK ((card_number IS NULL) <> (feature_number IS NULL));

-- A run is a build, a review, a fix, or the resolution of a conflict, which is always a feature's.
ALTER TABLE runs DROP CONSTRAINT runs_kind_check;
ALTER TABLE runs ADD CONSTRAINT runs_kind_check CHECK (kind IN ('build', 'review', 'fix', 'conflict'));

-- The default branch's commit a conflict brings in, recorded when it is handed out: its watch's
-- last commit then. A conflict starts from the feature's branch at from_commit, 0018's, and its
-- result's merge commit has these two as its parents. On a success it becomes the feature's
-- brought_in_commit, and on a failure in the job its bring_in_failed_at.
ALTER TABLE runs ADD COLUMN default_commit text CHECK (default_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');
ALTER TABLE runs ADD CHECK (kind <> 'conflict'
                            OR (feature_number IS NOT NULL AND from_commit IS NOT NULL AND default_commit IS NOT NULL));

-- What a conflict's agent chose against what git would have done, git show --remerge-diff of its
-- merge commit, as its worker sent it, cut beyond 1 MiB with a line saying so; data, shown and
-- handed to the review of the whole feature as text. Null for any other run.
ALTER TABLE runs ADD COLUMN remerge_diff text;

-- A feature has at most one run under way, as a card has, whatever the code does.
CREATE UNIQUE INDEX runs_one_under_way_per_feature ON runs (project_id, feature_number) WHERE finished_at IS NULL;
