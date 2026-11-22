-- Refusing a thin card: a card read as too thin is refused, marked on its issue with the attention
-- label and one comment from the app listing what is missing, and read again when a trusted account
-- edits it or comments on it after the refusal. How it is marked and read again is "Reading a card"
-- under "Jobs" in devflow/README.md, and why it is built so "Reading a card" in
-- devflow/docs/devflow-architecture.md.

-- Dev flow's comment on a card is kept with the reading it is for: the questions' comment of a
-- reading with questions, and now the refusal's of one too thin, by its id, so neither ever reads the
-- card again, and a card refused again gets a comment of its own.
ALTER TABLE card_readings DROP CONSTRAINT card_readings_check2;
ALTER TABLE card_readings ADD CONSTRAINT card_readings_comment_outcome
    CHECK (comment_id IS NULL OR outcome IN ('questions', 'too_thin'));

-- A refused card is read again when a trusted account edits it or comments on it after the
-- refusal: it is queued, and its newest reading still too thin makes it due a reading of it alone.
-- The comments that asked for it, each {login, body} exactly as written, are kept here for the
-- reader's brief until a reading comes back in its form; null otherwise. Which comments were taken
-- is comments_read_through, as for a stopped card.
ALTER TABLE cards ADD COLUMN read_again_comments jsonb CHECK (jsonb_typeof(read_again_comments) = 'array');
