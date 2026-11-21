-- The minutes each run's job carried, from its project's run_minutes when it was handed out. A run
-- is lost once it was handed out these minutes and 10 more ago and its worker has not been seen for
-- 10, so a run keeps the limit it started with whatever the setting is changed to meanwhile. See
-- "Jobs" in devflow/README.md.

-- Every run before this was handed out with the fixed 90; from now on each insert names its own.
ALTER TABLE runs ADD COLUMN run_minutes int NOT NULL DEFAULT 90 CHECK (run_minutes BETWEEN 10 AND 240);
ALTER TABLE runs ALTER COLUMN run_minutes DROP DEFAULT;
