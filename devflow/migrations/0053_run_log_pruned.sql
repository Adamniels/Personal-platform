-- A run's log is kept 90 days after the run finished; the run, its result, its figures and its
-- reason for ever. RunLogPruner deletes the lines of each run finished 90 days ago or more, once a
-- day, and marks the run here, so its page says its log was kept for 90 days rather than nothing, as
-- it does for a run from before logs were kept. How long a log is kept is "Jobs" in
-- devflow/README.md.

-- When the pruner deleted the run's lines; null while it has them, and for a run that never had any.
ALTER TABLE runs ADD COLUMN log_pruned_at timestamptz;
