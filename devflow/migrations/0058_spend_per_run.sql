-- The spend per run: the most a run of the project may cost, in whole US dollars, a limit beside its
-- minutes and turns in place of the fixed 25 every job carried. Every job carries it as
-- limits.budget_usd from the row as it was when the job was handed out, and the worker gives it to
-- Claude Code as --max-budget-usd, so a change takes effect at the next decision. On the
-- subscription it is Claude Code's estimate of the cost. See "Settings" under "Cards" and "Jobs" in
-- devflow/README.md.

-- Every project made before this has the 25 it was given until now. The check repeats the route's
-- range, in src/DevFlow/Projects/RunSettings.cs, for a row written around the route.
ALTER TABLE project_settings
    ADD COLUMN run_budget_usd int NOT NULL DEFAULT 25 CHECK (run_budget_usd BETWEEN 1 AND 100);
