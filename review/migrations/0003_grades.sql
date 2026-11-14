-- Every grade a card is given, kept from the first one. It is what later fits
-- FSRS's parameters to the owner's own memory, and a review's history cannot
-- be recreated. See "When a card comes back" in review/README.md.
--
-- One row per grade: the rating, when it was given, the days since the card's
-- last grade, and the card's FSRS state before and after, as
-- src/Review/Scheduling/Fsrs.cs computed it. Scoped to a user like every
-- table, with the door's one policy, and pointing at its card with user_id,
-- so it can only name a card of the same user, as cards and card_topics do.
-- Deleting a card deletes its grades.
--
-- The brain_* columns are for sending a grade to the brain, a later slice, and
-- stay null until then.
CREATE TABLE grades (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    card_id uuid NOT NULL,
    -- py-fsrs's Rating: Again 1, Hard 2, Good 3, Easy 4.
    rating smallint NOT NULL CHECK (rating BETWEEN 1 AND 4),
    reviewed_at timestamptz NOT NULL,
    -- Days since the card's last grade, with the fraction; 0 for its first.
    elapsed_days double precision NOT NULL CHECK (elapsed_days >= 0),
    state_before text NOT NULL CHECK (state_before IN ('new', 'learning', 'review', 'relearning')),
    stability_before double precision NULL,
    difficulty_before double precision NULL,
    -- A graded card is never new again.
    state_after text NOT NULL CHECK (state_after IN ('learning', 'review', 'relearning')),
    stability_after double precision NOT NULL,
    difficulty_after double precision NOT NULL,
    due_after timestamptz NOT NULL,
    brain_sent_at timestamptz NULL,
    brain_refused_at timestamptz NULL,
    brain_refused_status smallint NULL,
    FOREIGN KEY (user_id, card_id) REFERENCES cards (user_id, id) ON DELETE CASCADE
);

-- A card's history, in order; also what deleting a card cascades by.
CREATE INDEX grades_card ON grades (user_id, card_id, reviewed_at);
-- The grades not yet sent to the brain, nor refused by it.
CREATE INDEX grades_unsent ON grades (user_id, reviewed_at)
    WHERE brain_sent_at IS NULL AND brain_refused_at IS NULL;

ALTER TABLE grades ENABLE ROW LEVEL SECURITY;
ALTER TABLE grades FORCE ROW LEVEL SECURITY;
CREATE POLICY grades_user ON grades
    USING (user_id = nullif(current_setting('review.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('review.user_id', true), '')::uuid);

-- A grade, once given, is history: review_app writes one and never rewrites or
-- deletes it. It may only mark whether the brain has it. Deleting a card still
-- deletes its grades, since the cascade runs as the table's owner.
REVOKE UPDATE, DELETE ON grades FROM review_app;
GRANT UPDATE (brain_sent_at, brain_refused_at, brain_refused_status) ON grades TO review_app;