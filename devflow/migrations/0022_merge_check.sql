-- The merge check: whether a card that waits for the owner is still to be checked for a merge by
-- Dev flow. How it is checked is "The merge check" in devflow/README.md, and why it is built so
-- "The merge check" in devflow/docs/devflow-architecture.md.

-- Set as its review approves it, and kept while the check's reason may clear by itself, such as CI
-- still running or a read that failed, so the check reads it again on the next minute; cleared once
-- it merges or its reason is the owner's to answer.
ALTER TABLE cards ADD COLUMN merge_check_due boolean NOT NULL DEFAULT false;

-- The cards the check reads on each project's minute.
CREATE INDEX cards_merge_check_due ON cards (project_id) WHERE merge_check_due AND state = 'waiting_for_owner';
