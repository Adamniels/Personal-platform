-- A project's trusted accounts: the GitHub logins whose cards, edits and comments are instructions.
-- The owner's linked login is always one of them and is never stored here: it is added whenever the
-- list is read, so a relinked account is trusted under its new login at once and the old one is not.
-- What the list governs is "Cards" in devflow/docs/devflow-architecture.md, and the route that
-- changes it "Settings" in devflow/README.md.

-- The others, in the order the owner gave them, one per login whatever its case. A login's shape,
-- GitHub's rule, and one entry per login whatever the case are the route's, which names the field,
-- as a label's rules are; the table keeps only that the list is a flat list with no null in it.
-- Every project connected before this migration trusts its owner alone.
ALTER TABLE project_settings ADD COLUMN trusted_logins text[] NOT NULL DEFAULT '{}'
    CHECK (array_ndims(trusted_logins) IS NULL OR (array_ndims(trusted_logins) = 1 AND array_position(trusted_logins, NULL) IS NULL));
