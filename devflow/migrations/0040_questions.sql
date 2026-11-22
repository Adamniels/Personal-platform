-- Questions before starting: with the setting on, a card its reading found questions in waits for
-- the owner's answers on "Waiting for you"; off, it goes on the reader's recommendations, as "The
-- reading job" built it. The route that changes it is "Settings" in devflow/README.md, and what it
-- does "Reading a card" there and in devflow/docs/devflow-architecture.md.

-- On for a new project, and for every project there is, by the default.
ALTER TABLE project_settings ADD COLUMN questions boolean NOT NULL DEFAULT true;

-- A card its reading found questions in, with the setting on, needs the owner's answers: it waits
-- for them on "Waiting for you", nothing posted on GitHub, until he sends them. It is the owner's
-- turn, and inside a feature it holds back every later card, as a queued card does.
ALTER TABLE cards DROP CONSTRAINT cards_state_check;
ALTER TABLE cards ADD CONSTRAINT cards_state_check
    CHECK (state IN ('queued', 'reading', 'needs_answers', 'building', 'checking', 'reviewing', 'fixing', 'waiting_for_owner',
                     'done', 'stopped', 'refused', 'held', 'ignored'));

-- The owner's answers to a reading's questions, sent from "Waiting for you": one per question, in
-- the reading's order, each {answer, recommended}, the recommendation accepted or his own text,
-- kept with the reading that asked them, so a later reading's questions never take an earlier
-- reading's answers. comment_id holds the comment they were posted in, as him, so it never reads
-- the card again, when GitHub's answer named it. Null until he answers, and for any outcome but
-- questions.
ALTER TABLE card_readings
    ADD COLUMN answers jsonb CHECK (jsonb_typeof(answers) = 'array'),
    ADD COLUMN answered_at timestamptz,
    ADD CONSTRAINT card_readings_answered CHECK ((answers IS NULL) = (answered_at IS NULL)),
    ADD CONSTRAINT card_readings_answers_outcome CHECK (answers IS NULL OR outcome = 'questions');
