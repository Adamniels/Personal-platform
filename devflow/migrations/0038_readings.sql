-- Reading: before anything is built, a fresh agent reads a feature's cards together, or a card on
-- its own alone, with the code they touch, and answers for each whether it is ready, has
-- questions, or is too thin. How a reading is handed out and judged is "Jobs" in
-- devflow/README.md, and why it is built so "Reading a card" in devflow/docs/devflow-architecture.md.

-- A card is reading from when its reading is handed out until its result, and refused when it was
-- read as too thin to build, with what is missing as its reason; "Refusing a thin card" marks it.
ALTER TABLE cards DROP CONSTRAINT cards_state_check;
ALTER TABLE cards ADD CONSTRAINT cards_state_check
    CHECK (state IN ('queued', 'reading', 'building', 'checking', 'reviewing', 'fixing', 'waiting_for_owner', 'done',
                     'stopped', 'refused', 'held', 'ignored'));

-- A reading is a run of its own: a feature's, by its parent's number, which one at a time is
-- already held by runs_one_under_way_per_feature, or a card's on its own, by its number. It names no
-- commit and pushes none.
ALTER TABLE runs DROP CONSTRAINT runs_kind_check;
ALTER TABLE runs ADD CONSTRAINT runs_kind_check
    CHECK (kind IN ('build', 'review', 'fix', 'conflict', 'feature_review', 'feature_fix', 'read'));
ALTER TABLE runs ADD CHECK (kind <> 'read' OR head_commit IS NULL);

-- What a reading answered for each card it read: one row per card and reading run, made when the
-- run is handed out, with the card's body SHA-256 and title as it was handed, which "Reading again"
-- compares against; its outcome, questions and what is missing when the result came back in its
-- form. A run that ends any other way leaves its rows without an outcome, the record of which cards
-- it was handed. A card's reading is its newest with an outcome.
-- The note of the code the cards touch is the run's report's touches, as a card's handover note is
-- its run's report's handover. It names its run by id and its card by number, as a run names its
-- card, so a card removed when its label is taken off leaves its readings behind; both go with
-- their project.
CREATE TABLE card_readings (
    run_id uuid NOT NULL,
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    card_number int NOT NULL CHECK (card_number > 0),
    -- The card as the reading was handed it.
    body_sha256 bytea NOT NULL CHECK (octet_length(body_sha256) = 32),
    title text NOT NULL,
    -- What the reader answered, null until its result came back in its form.
    outcome text CHECK (outcome IN ('ready', 'questions', 'too_thin')),
    -- The questions, each {question, recommendation, why}, and what is missing, as the reading
    -- holds them; data, shown as text.
    questions jsonb CHECK (jsonb_typeof(questions) = 'array'),
    missing jsonb CHECK (jsonb_typeof(missing) = 'array'),
    -- When the result came back.
    read_at timestamptz,
    -- Dev flow's comment on a card read with questions, listing each and the recommendation chosen,
    -- by its id, once posted as the app; null until then, and for any other outcome.
    comment_id bigint CHECK (comment_id > 0),
    PRIMARY KEY (run_id, card_number),
    CHECK ((outcome IS NULL) = (read_at IS NULL)),
    CHECK ((outcome IS NULL) = (questions IS NULL) AND (outcome IS NULL) = (missing IS NULL)),
    CHECK (comment_id IS NULL OR outcome = 'questions'),
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

-- A card's readings, newest first, which deciding whether it was read and its page read.
CREATE INDEX card_readings_card ON card_readings (project_id, card_number, read_at DESC);

ALTER TABLE card_readings ENABLE ROW LEVEL SECURITY;
ALTER TABLE card_readings FORCE ROW LEVEL SECURITY;
CREATE POLICY card_readings_user ON card_readings
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
