-- The model each run's job was handed out with, recorded in the transaction that takes the work, as
-- run_minutes is, so "Running now" names the model a run uses whatever its project's setting is
-- changed to meanwhile. Null for a check, which runs no model, and for every run handed out before
-- this. See "Jobs" in devflow/README.md.
ALTER TABLE runs ADD COLUMN model text CHECK (model IN ('opus', 'sonnet', 'haiku'));
