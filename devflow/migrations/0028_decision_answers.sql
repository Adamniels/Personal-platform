-- Decision answers: the owner's answer to each decision a card of a feature marked for him, given on
-- the feature's page. How one is given is "A feature's page" in devflow/README.md, and why it stays
-- in Dev flow and never reaches the code host "A feature's page" in
-- devflow/docs/devflow-architecture.md.

-- One answer per card, run and decision: the run whose report marked it, and its place among that
-- report's decisions, from 0, so a later report's decisions are answered again. Kept as first
-- given; answering again changes nothing.
CREATE TABLE decision_answers (
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    card_number int NOT NULL CHECK (card_number > 0),
    run_id uuid NOT NULL REFERENCES runs (id) ON DELETE CASCADE,
    position int NOT NULL CHECK (position >= 0),
    -- fine is "Fine as it is"; sent_back is "Send back", which its own slice gives.
    answer text NOT NULL CHECK (answer IN ('fine', 'sent_back')),
    answered_at timestamptz NOT NULL,
    PRIMARY KEY (project_id, run_id, position),
    -- Follows the project, as every scoped table does.
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE decision_answers ENABLE ROW LEVEL SECURITY;
ALTER TABLE decision_answers FORCE ROW LEVEL SECURITY;
CREATE POLICY decision_answers_user ON decision_answers
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
