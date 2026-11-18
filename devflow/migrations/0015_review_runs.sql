-- Review runs: a run may review the commit a build left on a card's pull request. How a review is
-- handed out is "Review" in devflow/README.md, and why it is built so "Review" in
-- devflow/docs/devflow-architecture.md.

-- A run is a build or a review. A review run's head_commit is the commit it was handed, recorded
-- when it is handed out, since a review leaves the branch where it was; the worker's result must
-- name the same commit.
ALTER TABLE runs DROP CONSTRAINT runs_kind_check;
ALTER TABLE runs ADD CONSTRAINT runs_kind_check CHECK (kind IN ('build', 'review'));
ALTER TABLE runs ADD CHECK (kind <> 'review' OR head_commit IS NOT NULL);

-- The builds of a card that succeeded, newest first, which a card's head commit is read from.
CREATE INDEX runs_card_builds ON runs (project_id, card_number, started_at DESC) WHERE kind = 'build' AND result = 'succeeded';
