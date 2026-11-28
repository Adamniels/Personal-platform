-- "Run now anyway": the owner's override of a project's schedules for one feature or one card on its
-- own. While it holds, every job of that feature, its cards' among them, or of that card is handed
-- out outside the schedules; it skips the schedules and nothing else, so the pauses still hold it. It
-- lasts until the feature or card has nothing left to run, when the dispatcher deletes it in the
-- transaction that found that, so a card added or sent back later keeps to the schedules again. What
-- it covers and when it ends are "Schedules" under "Jobs" in devflow/README.md; why it is a row the
-- dispatcher deletes, "Handing out work" in devflow/docs/devflow-architecture.md.

CREATE TABLE run_now (
    project_id uuid NOT NULL,
    user_id uuid NOT NULL,
    -- The feature's parent issue number, or the card's, exactly one of the two.
    feature_number int CHECK (feature_number > 0),
    card_number int CHECK (card_number > 0),
    created_at timestamptz NOT NULL,
    CHECK ((feature_number IS NULL) <> (card_number IS NULL)),
    -- One per feature and one per card; the null of the other column never collides.
    UNIQUE (project_id, feature_number),
    UNIQUE (project_id, card_number),
    -- Follows the project, gone with it and renumbered with it, as project_schedules is.
    FOREIGN KEY (project_id, user_id) REFERENCES projects (id, user_id) ON DELETE CASCADE ON UPDATE CASCADE
);

ALTER TABLE run_now ENABLE ROW LEVEL SECURITY;
ALTER TABLE run_now FORCE ROW LEVEL SECURITY;
CREATE POLICY run_now_user ON run_now
    USING (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid)
    WITH CHECK (user_id = nullif(current_setting('devflow.user_id', true), '')::uuid);
