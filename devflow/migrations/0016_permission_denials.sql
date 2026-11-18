-- The actions an agent was refused in a run, as Claude Code listed them. A build's fail its run; a
-- review's are recorded, since a reviewer cannot write by construction. See "Review" in
-- devflow/README.md.

-- As the worker sent them, an array, empty when none were refused; null for a run whose worker sent
-- none, a run before this column among them.
ALTER TABLE runs ADD COLUMN permission_denials jsonb CHECK (permission_denials IS NULL OR jsonb_typeof(permission_denials) = 'array');
