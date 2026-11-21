-- Fixing a whole feature: a changes verdict on a feature sends it to a fresh developer agent on the
-- feature's branch, in a run for the feature, for at most three rounds, each followed by a new
-- review of the whole feature; the owner's comment on the feature's pull request starts a fix that
-- is not one. How a feature's fix is handed out, judged and reviewed again is "Fixing a feature" in
-- devflow/README.md, and why it is built so "Fixing a feature" in
-- devflow/docs/devflow-architecture.md.

-- A run is a build, a review, a fix, a conflict's resolution, a whole feature's review, or a whole
-- feature's fix, which is always a feature's and always starts from the commit it was handed, as a
-- card's fix does. Its fix_round, fix_review_id and owner_comments are 0018's and 0019's, its
-- review one in feature_reviews.
ALTER TABLE runs DROP CONSTRAINT runs_kind_check;
ALTER TABLE runs ADD CONSTRAINT runs_kind_check CHECK (kind IN ('build', 'review', 'fix', 'conflict', 'feature_review', 'feature_fix'));
ALTER TABLE runs ADD CHECK (kind <> 'feature_fix' OR (feature_number IS NOT NULL AND from_commit IS NOT NULL));

-- What the feature's next fix answers, set as it moves to fixing and copied onto the fix's run
-- when it is handed out: its round, 1 to 3, null when the owner's comment started it; the review
-- it answers, by its run in feature_reviews, null when there is none; and the owner's comments,
-- each exactly as he wrote it, null when none. features.fix_round, 0024's, counts the rounds the
-- feature's reviews started.
ALTER TABLE features ADD COLUMN next_fix_round int CHECK (next_fix_round BETWEEN 1 AND 3);
ALTER TABLE features ADD COLUMN fix_review_id uuid;
ALTER TABLE features ADD COLUMN fix_comments jsonb CHECK (fix_comments IS NULL OR jsonb_typeof(fix_comments) = 'array');

-- The newest of the owner's comments on the feature's pull request already taken, by GitHub's id,
-- which only grows, so none is taken twice and none written while a run was under way is lost.
-- Null before any was taken, when every one of his counts.
ALTER TABLE features ADD COLUMN comments_read_through bigint;

-- The commit the feature's last fix that failed in the job was handed, null after a fix succeeded
-- or a new round began: a second failure from the same commit leaves the feature waiting for the
-- owner, and its readiness leaves it so while its branch is still there.
ALTER TABLE features ADD COLUMN fix_failed_at text CHECK (fix_failed_at ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');
