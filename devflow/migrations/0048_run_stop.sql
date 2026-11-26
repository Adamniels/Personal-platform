-- Stopping a run: the owner marks a run under way to stop, and its worker's next post to the run's
-- log is answered stop. How the route and the worker behave is "Jobs" in devflow/README.md.

-- When the owner first asked the run to stop, null while nobody has. Set only on a run under way; a
-- second stop keeps the first time.
ALTER TABLE runs ADD COLUMN stop_requested_at timestamptz;
