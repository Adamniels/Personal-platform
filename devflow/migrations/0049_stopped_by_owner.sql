-- A run the owner stopped, and what it leaves: the run recorded stopped_by_owner, a card stopped
-- "Stopped by you", a feature held. A stop is never a failure, so nothing a failure counts is
-- touched. How it is recorded is "Jobs" and "Stops" in devflow/README.md, and why it is no failure
-- "Stops" in devflow/docs/devflow-architecture.md.

-- stopped_by_owner beside stopped, which already means the agent's report said it stopped.
ALTER TABLE runs DROP CONSTRAINT runs_result_check;
ALTER TABLE runs ADD CONSTRAINT runs_result_check CHECK (result IN ('succeeded', 'stopped', 'check_failed', 'failed_in_card',
                                                                    'failed_outside_card', 'report_invalid', 'lost',
                                                                    'stopped_by_owner'));

-- Whether the card's stop, the one at stopped_at, was the owner's, and the kind and fix round of the
-- run he stopped, so running it again knows where it goes back to. Every other way a card stops sets
-- them back, so they always describe its current stop, and are read only while it is stopped.
ALTER TABLE cards ADD COLUMN stopped_by_owner boolean NOT NULL DEFAULT false;
ALTER TABLE cards ADD COLUMN stopped_kind text CHECK (stopped_kind IN ('build', 'review', 'fix'));
ALTER TABLE cards ADD COLUMN stopped_round int CHECK (stopped_round BETWEEN 1 AND 10);
ALTER TABLE cards ADD CONSTRAINT cards_stopped_by_owner_kind CHECK (stopped_by_owner = (stopped_kind IS NOT NULL));
ALTER TABLE cards ADD CONSTRAINT cards_stopped_round_of_a_fix CHECK (stopped_round IS NULL OR stopped_kind = 'fix');

-- When the owner stopped one of the feature's own runs, a review, a fix or a conflict, which holds
-- it: none of its jobs is handed out and its readiness is not decided while it is set. Null while
-- it is not held.
ALTER TABLE features ADD COLUMN stopped_by_owner_at timestamptz;
