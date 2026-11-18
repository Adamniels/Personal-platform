-- Fix rounds: a run may fix a card on its branch, from its head, after its review asked for
-- changes, for at most three rounds. How a fix is handed out and judged is "Fixing" in
-- devflow/README.md, and why it is built so "Fixing" in devflow/docs/devflow-architecture.md.

-- A run is a build, a review or a fix.
ALTER TABLE runs DROP CONSTRAINT runs_kind_check;
ALTER TABLE runs ADD CONSTRAINT runs_kind_check CHECK (kind IN ('build', 'review', 'fix'));

-- The commit a fix was handed, recorded when it is handed out: the newest one a build or a fix of
-- the card left on its branch. The worker starts from it and from nothing else, and a fix whose
-- result leaves the branch there changed nothing, which fails it in the card.
ALTER TABLE runs ADD COLUMN from_commit text CHECK (from_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');
ALTER TABLE runs ADD CHECK (kind <> 'fix' OR from_commit IS NOT NULL);

-- The round a fix is, 1 to 3; null for a run that is not a fix, and for a fix the owner's comment
-- started, which is not a round.
ALTER TABLE runs ADD COLUMN fix_round int CHECK (fix_round BETWEEN 1 AND 3);

-- The review a fix answers, by its run: the one whose changes started its round. Null for a run
-- that is not a fix, and for one that answers no review. No key to reviews, as a run's card is not
-- one to cards: a fix outlives nothing it names.
ALTER TABLE runs ADD COLUMN fix_review_id uuid;

-- A card's head is the commit its newest build or fix that succeeded left.
DROP INDEX runs_card_builds;
CREATE INDEX runs_card_heads ON runs (project_id, card_number, started_at DESC) WHERE kind IN ('build', 'fix') AND result = 'succeeded';

-- How many fix rounds reviews have started for the card; a changes verdict once it has used all
-- three stops it.
ALTER TABLE cards ADD COLUMN fix_rounds int NOT NULL DEFAULT 0 CHECK (fix_rounds BETWEEN 0 AND 3);

-- What the card's next fix answers, set as it moves to fixing and copied onto the fix's run when
-- it is handed out: its round, null when the owner's comment started it, and the review it
-- answers, null when there is none.
ALTER TABLE cards ADD COLUMN fix_round int CHECK (fix_round BETWEEN 1 AND 3);
ALTER TABLE cards ADD COLUMN fix_review_id uuid;
