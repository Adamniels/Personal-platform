-- Sending a failed check back: the owner's comment on a check of the newest version of .devflow/
-- that failed starts a new setup, on the next free devflow/setup-<k>, whose runs' brief holds the
-- files as the check saw them, the check's output and his comment. How it is offered and handed out
-- is "Setting up a project" in devflow/README.md, and why "Setting up a project" in
-- devflow/docs/devflow-architecture.md.

-- sent_back_check is the check it was sent back from, whose exit code and output the brief quotes;
-- sent_back_comment the owner's comment, trimmed; sent_back_recipe and sent_back_project_file the
-- two files at the commit checked, as Dev flow read them when he sent it back, each null when it
-- was missing there. All null for a setup the project needed because .devflow/ was not valid.
ALTER TABLE setups ADD COLUMN sent_back_check uuid REFERENCES recipe_checks (run_id) ON DELETE CASCADE;
ALTER TABLE setups ADD COLUMN sent_back_comment text CHECK (length(sent_back_comment) BETWEEN 1 AND 4000);
ALTER TABLE setups ADD COLUMN sent_back_recipe text CHECK (length(sent_back_recipe) <= 65536);
ALTER TABLE setups ADD COLUMN sent_back_project_file text CHECK (length(sent_back_project_file) <= 65536);
ALTER TABLE setups ADD CONSTRAINT setups_sent_back_whole CHECK (
    (sent_back_check IS NULL) = (sent_back_comment IS NULL)
    AND (sent_back_check IS NOT NULL OR (sent_back_recipe IS NULL AND sent_back_project_file IS NULL)));

-- A failed check is sent back once.
CREATE UNIQUE INDEX setups_sent_back_once ON setups (sent_back_check) WHERE sent_back_check IS NOT NULL;
